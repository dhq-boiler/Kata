using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.Cpp;

public sealed record CppTargetProject(string VcxprojPath, string ProjectDir, string ProjectName);

public static class CppCliRefactorEngine
{
    public static IReadOnlyList<DocumentChange> AddGhostType(
        CppTargetProject target,
        AddGhostTypeIntent intent)
    {
        var kindDecl = intent.Kind switch
        {
            TypeKind.Interface => "public interface class",
            TypeKind.Struct => "public value struct",
            TypeKind.Enum => "public enum class",
            TypeKind.Record => "public ref class",
            _ => "public ref class",
        };

        var body = intent.Kind == TypeKind.Enum ? "{ }" : "{ };";

        var nsFull = intent.Namespace.FullName;
        var nsCpp = string.IsNullOrEmpty(nsFull) ? string.Empty : nsFull.Replace(".", "::");

        var sb = new StringBuilder();
        sb.AppendLine("#pragma once");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.Append("namespace ").AppendLine(nsCpp);
            sb.AppendLine("{");
            sb.Append("    ").Append(kindDecl).Append(' ').Append(intent.ProposedName).Append(' ').AppendLine(body);
            sb.AppendLine("}");
        }
        else
        {
            sb.Append(kindDecl).Append(' ').Append(intent.ProposedName).Append(' ').AppendLine(body);
        }

        var relative = $"{intent.ProposedName}.h";
        var absolute = Path.Combine(target.ProjectDir, relative);

        var originalVcxproj = File.ReadAllText(target.VcxprojPath);
        var updatedVcxproj = InsertClIncludeIntoVcxproj(originalVcxproj, relative);

        return new DocumentChange[]
        {
            new(absolute, DocumentChangeKind.Added, OldText: null, NewText: sb.ToString()),
            new(target.VcxprojPath, DocumentChangeKind.Modified, OldText: originalVcxproj, NewText: updatedVcxproj),
        };
    }

    private static string InsertClIncludeIntoVcxproj(string vcxprojXml, string relative)
    {
        var doc = XDocument.Parse(vcxprojXml, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException("vcxproj root missing.");
        var ns = root.GetDefaultNamespace();

        var itemGroup = root.Descendants(ns + "ClInclude").FirstOrDefault()?.Parent;
        if (itemGroup is null)
        {
            itemGroup = new XElement(ns + "ItemGroup");
            root.Add(itemGroup);
        }

        itemGroup.Add(new XElement(
            ns + "ClInclude",
            new XAttribute("Include", relative.Replace('/', '\\'))));

        return doc.ToString();
    }

    public static bool TryFindTargetByNamespace(
        SolutionModel model,
        NamespaceRef ns,
        out CppTargetProject? target)
    {
        target = null;
        var nsFull = ns.FullName ?? string.Empty;

        ProjectModel? best = null;
        var bestLen = -1;
        foreach (var p in model.Projects)
        {
            if (!string.Equals(p.LanguageId, "cpp-cli", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nsFull == p.Name || nsFull.StartsWith(p.Name + ".", StringComparison.Ordinal))
            {
                if (p.Name.Length > bestLen)
                {
                    best = p;
                    bestLen = p.Name.Length;
                }
            }
        }

        if (best is null) return false;

        var dir = Path.GetDirectoryName(best.FilePath);
        if (dir is null) return false;

        target = new CppTargetProject(best.FilePath, dir, best.Name);
        return true;
    }

    public static IReadOnlyList<DocumentChange> Rename(
        CppTargetProject target,
        string oldName,
        string newName)
    {
        var pattern = new Regex($@"\b{Regex.Escape(oldName)}\b", RegexOptions.Compiled);
        var changes = new List<DocumentChange>();
        foreach (var file in EnumerateSourceFiles(target.ProjectDir))
        {
            string original;
            try { original = File.ReadAllText(file); }
            catch { continue; }

            var updated = pattern.Replace(original, newName);
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(file, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }
        return changes;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectDir)
    {
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".h" or ".hpp" or ".cpp" or ".cxx"))
            {
                continue;
            }
            if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains(Path.DirectorySeparatorChar + "x64" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains(Path.DirectorySeparatorChar + "x86" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains(Path.DirectorySeparatorChar + ".vs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    public static IReadOnlyList<DocumentChange> ExtractInterface(
        CppTargetProject target,
        ExtractInterfaceIntent intent,
        string sourceTypeName)
    {
        var headerPath = FindDefiningHeader(target.ProjectDir, sourceTypeName);
        if (headerPath is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var originalHeader = File.ReadAllText(headerPath);
        var tokens = CppCliLexer.Tokenize(originalHeader);
        var decls = CppCliDeclParser.Parse(tokens);
        var sourceDecl = decls.FirstOrDefault(d => d.Name == sourceTypeName);
        if (sourceDecl is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var wantedNames = new HashSet<string>(
            intent.Members.Select(m => m.Signature.IndexOf('(') is var p and >= 0 ? m.Signature[..p] : m.Signature),
            StringComparer.Ordinal);

        var selected = sourceDecl.Members
            .Where(m => wantedNames.Contains(m.Name))
            .Where(m => m.Kind is MemberKind.Method or MemberKind.Property or MemberKind.Event)
            .ToList();

        var interfaceNamespace = intent.TargetNamespace?.FullName
                                 ?? sourceDecl.NamespaceFullName;

        var interfaceSource = BuildInterfaceSource(intent.ProposedInterfaceName, interfaceNamespace, selected);
        var interfaceRelative = $"{intent.ProposedInterfaceName}.h";
        var interfaceAbsolute = Path.Combine(target.ProjectDir, interfaceRelative);

        var updatedHeader = AddInterfaceToBaseList(originalHeader, sourceTypeName, intent.ProposedInterfaceName);

        var vcxprojOriginal = File.ReadAllText(target.VcxprojPath);
        var vcxprojUpdated = InsertClIncludeIntoVcxproj(vcxprojOriginal, interfaceRelative);

        return new DocumentChange[]
        {
            new(interfaceAbsolute, DocumentChangeKind.Added, OldText: null, NewText: interfaceSource),
            new(headerPath, DocumentChangeKind.Modified, OldText: originalHeader, NewText: updatedHeader),
            new(target.VcxprojPath, DocumentChangeKind.Modified, OldText: vcxprojOriginal, NewText: vcxprojUpdated),
        };
    }

    private static string? FindDefiningHeader(string projectDir, string typeName)
    {
        var pattern = new Regex(
            $@"\b(ref|value|interface|enum)\s+(class|struct)\s+{Regex.Escape(typeName)}\b\s*[:{{]",
            RegexOptions.Compiled);

        foreach (var f in EnumerateSourceFiles(projectDir))
        {
            if (!f.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string text;
            try { text = File.ReadAllText(f); }
            catch { continue; }
            if (pattern.IsMatch(text)) return f;
        }
        return null;
    }

    private static string BuildInterfaceSource(
        string interfaceName,
        string namespaceFullName,
        IReadOnlyList<CppMember> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#pragma once");
        sb.AppendLine();

        var nsCpp = string.IsNullOrEmpty(namespaceFullName) ? string.Empty : namespaceFullName.Replace(".", "::");
        var indent = string.IsNullOrEmpty(nsCpp) ? string.Empty : "    ";

        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.Append("namespace ").AppendLine(nsCpp);
            sb.AppendLine("{");
        }

        sb.Append(indent).Append("public interface class ").AppendLine(interfaceName);
        sb.Append(indent).AppendLine("{");
        foreach (var m in members)
        {
            var line = RenderInterfaceMember(m);
            if (line is not null)
            {
                sb.Append(indent).Append("    ").AppendLine(line);
            }
        }
        sb.Append(indent).AppendLine("};");

        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string? RenderInterfaceMember(CppMember m)
    {
        var type = string.IsNullOrEmpty(m.ReturnTypeDisplay) ? "void" : m.ReturnTypeDisplay;
        return m.Kind switch
        {
            MemberKind.Method => $"{type} {m.Name}({RenderParameters(m.Parameters)});",
            MemberKind.Property => $"property {type} {m.Name};",
            MemberKind.Event => $"event {type} {m.Name};",
            _ => null,
        };
    }

    private static string RenderParameters(IReadOnlyList<CppParameter>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return string.Empty;
        return string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"));
    }

    private static string AddInterfaceToBaseList(string text, string typeName, string interfaceName)
    {
        var pattern = new Regex(
            $@"\b(class|struct)\s+{Regex.Escape(typeName)}\b(\s*)(:|\{{)",
            RegexOptions.Compiled);

        return pattern.Replace(text, m =>
        {
            var keyword = m.Groups[1].Value;
            var next = m.Groups[3].Value;
            return next == "{"
                ? $"{keyword} {typeName} : public {interfaceName} {{"
                : $"{keyword} {typeName} : public {interfaceName},";
        }, count: 1);
    }

    public static IReadOnlyList<DocumentChange> ExtractSuperclass(
        CppTargetProject target,
        ExtractSuperclassIntent intent,
        string sourceTypeName)
    {
        var headerPath = FindDefiningHeader(target.ProjectDir, sourceTypeName);
        if (headerPath is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var originalHeader = File.ReadAllText(headerPath);
        var tokens = CppCliLexer.Tokenize(originalHeader);
        var decls = CppCliDeclParser.Parse(tokens);
        var sourceDecl = decls.FirstOrDefault(d => d.Name == sourceTypeName);
        if (sourceDecl is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var wantedNames = new HashSet<string>(
            intent.Members.Select(m => m.Signature.IndexOf('(') is var p and >= 0 ? m.Signature[..p] : m.Signature),
            StringComparer.Ordinal);
        var selected = sourceDecl.Members
            .Where(m => wantedNames.Contains(m.Name))
            .Where(m => m.Kind is MemberKind.Method or MemberKind.Property or MemberKind.Event or MemberKind.Field)
            .ToList();

        var superNamespace = intent.TargetNamespace?.FullName ?? sourceDecl.NamespaceFullName;
        var superText = BuildSuperclassCppSource(intent.ProposedSuperclassName, superNamespace, selected);
        var superRelative = $"{intent.ProposedSuperclassName}.h";
        var superAbsolute = Path.Combine(target.ProjectDir, superRelative);

        var updatedHeader = AddInterfaceToBaseList(originalHeader, sourceTypeName, intent.ProposedSuperclassName);

        var vcxprojOriginal = File.ReadAllText(target.VcxprojPath);
        var vcxprojUpdated = InsertClIncludeIntoVcxproj(vcxprojOriginal, superRelative);

        return new DocumentChange[]
        {
            new(superAbsolute, DocumentChangeKind.Added, OldText: null, NewText: superText),
            new(headerPath, DocumentChangeKind.Modified, OldText: originalHeader, NewText: updatedHeader),
            new(target.VcxprojPath, DocumentChangeKind.Modified, OldText: vcxprojOriginal, NewText: vcxprojUpdated),
        };
    }

    private static string BuildSuperclassCppSource(
        string superclassName,
        string namespaceFullName,
        IReadOnlyList<CppMember> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#pragma once");
        sb.AppendLine();

        var nsCpp = string.IsNullOrEmpty(namespaceFullName) ? string.Empty : namespaceFullName.Replace(".", "::");
        var indent = string.IsNullOrEmpty(nsCpp) ? string.Empty : "    ";

        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.Append("namespace ").AppendLine(nsCpp);
            sb.AppendLine("{");
        }

        sb.Append(indent).Append("public ref class ").Append(superclassName).AppendLine(" abstract");
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("public:");
        foreach (var m in members)
        {
            var line = RenderSuperclassMember(m);
            if (line is not null)
            {
                sb.Append(indent).Append("    ").AppendLine(line);
            }
        }
        sb.Append(indent).AppendLine("};");

        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string? RenderSuperclassMember(CppMember m)
    {
        var type = string.IsNullOrEmpty(m.ReturnTypeDisplay) ? "void" : m.ReturnTypeDisplay;
        return m.Kind switch
        {
            MemberKind.Method => $"virtual {type} {m.Name}({RenderParameters(m.Parameters)});",
            MemberKind.Property => $"property {type} {m.Name};",
            MemberKind.Event => $"event {type} {m.Name};",
            MemberKind.Field => $"{type} {m.Name};",
            _ => null,
        };
    }

    public static IReadOnlyList<DocumentChange> ExtractClass(
        CppTargetProject target,
        ExtractClassIntent intent,
        string sourceTypeName)
    {
        var headerPath = FindDefiningHeader(target.ProjectDir, sourceTypeName);
        if (headerPath is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var originalHeader = File.ReadAllText(headerPath);
        var tokens = CppCliLexer.Tokenize(originalHeader);
        var decls = CppCliDeclParser.Parse(tokens);
        var sourceDecl = decls.FirstOrDefault(d => d.Name == sourceTypeName);
        if (sourceDecl is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var wantedNames = new HashSet<string>(
            intent.Members.Select(m => m.Signature.IndexOf('(') is var p and >= 0 ? m.Signature[..p] : m.Signature),
            StringComparer.Ordinal);
        var selected = sourceDecl.Members
            .Where(m => wantedNames.Contains(m.Name))
            .Where(m => m.Kind is MemberKind.Method or MemberKind.Property or MemberKind.Event or MemberKind.Field)
            .ToList();

        var newNamespace = intent.TargetNamespace?.FullName ?? sourceDecl.NamespaceFullName;
        var newClassText = BuildExtractedClassCppSource(intent.ProposedClassName, newNamespace, selected);
        var newRelative = $"{intent.ProposedClassName}.h";
        var newAbsolute = Path.Combine(target.ProjectDir, newRelative);

        var updatedHeader = AddDelegateFieldToClassBody(
            originalHeader, sourceTypeName, intent.ProposedClassName, intent.DelegatePropertyName);

        var vcxprojOriginal = File.ReadAllText(target.VcxprojPath);
        var vcxprojUpdated = InsertClIncludeIntoVcxproj(vcxprojOriginal, newRelative);

        return new DocumentChange[]
        {
            new(newAbsolute, DocumentChangeKind.Added, OldText: null, NewText: newClassText),
            new(headerPath, DocumentChangeKind.Modified, OldText: originalHeader, NewText: updatedHeader),
            new(target.VcxprojPath, DocumentChangeKind.Modified, OldText: vcxprojOriginal, NewText: vcxprojUpdated),
        };
    }

    private static string BuildExtractedClassCppSource(
        string className,
        string namespaceFullName,
        IReadOnlyList<CppMember> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#pragma once");
        sb.AppendLine();

        var nsCpp = string.IsNullOrEmpty(namespaceFullName) ? string.Empty : namespaceFullName.Replace(".", "::");
        var indent = string.IsNullOrEmpty(nsCpp) ? string.Empty : "    ";

        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.Append("namespace ").AppendLine(nsCpp);
            sb.AppendLine("{");
        }

        sb.Append(indent).Append("public ref class ").AppendLine(className);
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("public:");
        foreach (var m in members)
        {
            var line = RenderSuperclassMember(m);
            if (line is not null)
            {
                sb.Append(indent).Append("    ").AppendLine(line);
            }
        }
        sb.Append(indent).AppendLine("};");

        if (!string.IsNullOrEmpty(nsCpp))
        {
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string AddDelegateFieldToClassBody(
        string text, string typeName, string newClassName, string delegatePropertyName)
    {
        // Insert "public: NewClass^ Delegate;" after "class TypeName ... {".
        var pattern = new Regex(
            $@"\b(class|struct)\s+{Regex.Escape(typeName)}\b[^{{]*\{{",
            RegexOptions.Compiled);
        return pattern.Replace(text, m =>
            $"{m.Value}\n    public:\n        {newClassName}^ {delegatePropertyName};", count: 1);
    }

    public static IReadOnlyList<DocumentChange> RemoveSubclass(
        CppTargetProject target,
        string subclassName,
        string baseName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(subclassName) || !identPattern.IsMatch(baseName))
        {
            return Array.Empty<DocumentChange>();
        }

        var replace = new Regex($@"\b{Regex.Escape(subclassName)}\b", RegexOptions.Compiled);
        var declProbe = new Regex(
            $@"\b(ref|value|interface|enum)\s+(class|struct)\s+{Regex.Escape(subclassName)}\b\s*[:{{]",
            RegexOptions.Compiled);

        var subclassFile = FindDefiningHeader(target.ProjectDir, subclassName);
        var changes = new List<DocumentChange>();

        foreach (var f in EnumerateSourceFiles(target.ProjectDir))
        {
            string original;
            try { original = File.ReadAllText(f); }
            catch { continue; }

            // Skip the subclass file — it will be deleted wholesale below.
            if (subclassFile is not null && string.Equals(f, subclassFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var updated = replace.Replace(original, baseName);
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(f, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }

        if (subclassFile is not null)
        {
            changes.Add(new DocumentChange(
                subclassFile,
                DocumentChangeKind.Deleted,
                OldText: File.ReadAllText(subclassFile),
                NewText: null));
        }

        return changes;
    }

    public static IReadOnlyList<DocumentChange> CollapseHierarchy(
        CppTargetProject target,
        string subclassName,
        string parentName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(subclassName) || !identPattern.IsMatch(parentName))
        {
            return Array.Empty<DocumentChange>();
        }

        var subHeader = FindDefiningHeader(target.ProjectDir, subclassName);
        var parentHeader = FindDefiningHeader(target.ProjectDir, parentName);
        if (subHeader is null || parentHeader is null)
        {
            return Array.Empty<DocumentChange>();
        }

        // Parse the subclass to enumerate its declared members.
        var subOriginal = File.ReadAllText(subHeader);
        var subTokens = CppCliLexer.Tokenize(subOriginal);
        var subDecls = CppCliDeclParser.Parse(subTokens);
        var subDecl = subDecls.FirstOrDefault(d => d.Name == subclassName);

        var parentOriginal = File.ReadAllText(parentHeader);

        // Insert the subclass members into the parent's class body (as a "public:" block).
        var parentUpdated = parentOriginal;
        if (subDecl is not null && subDecl.Members.Count > 0)
        {
            parentUpdated = AppendMembersToClassBody(parentUpdated, parentName, subDecl.Members);
        }

        // Rewrite any Sub identifier that survived (in the members' return types, or elsewhere in parent).
        var replace = new Regex($@"\b{Regex.Escape(subclassName)}\b", RegexOptions.Compiled);
        parentUpdated = replace.Replace(parentUpdated, parentName);

        var changes = new List<DocumentChange>
        {
            new(parentHeader, DocumentChangeKind.Modified, OldText: parentOriginal, NewText: parentUpdated),
            new(subHeader, DocumentChangeKind.Deleted, OldText: subOriginal, NewText: null),
        };

        foreach (var f in EnumerateSourceFiles(target.ProjectDir))
        {
            if (string.Equals(f, subHeader, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(f, parentHeader, StringComparison.OrdinalIgnoreCase)) continue;

            string original;
            try { original = File.ReadAllText(f); }
            catch { continue; }

            var updated = replace.Replace(original, parentName);
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(f, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }

        return changes;
    }

    private static string AppendMembersToClassBody(
        string text,
        string typeName,
        IReadOnlyList<CppMember> members)
    {
        // Match the opening brace of the class body: `class Name ... {`
        var pattern = new Regex(
            $@"\b(class|struct)\s+{Regex.Escape(typeName)}\b[^{{]*\{{",
            RegexOptions.Compiled);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.Append("    public:");
        foreach (var m in members)
        {
            var line = RenderSuperclassMember(m);
            if (line is not null)
            {
                sb.AppendLine().Append("        ").Append(line);
            }
        }

        var inject = sb.ToString();
        return pattern.Replace(text, m => m.Value + inject, count: 1);
    }

    public static IReadOnlyList<DocumentChange> MoveMembersBetweenClasses(
        CppTargetProject target,
        string fromClassName,
        string toClassName,
        IReadOnlyList<string> wantedMemberNames)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(fromClassName) || !identPattern.IsMatch(toClassName))
        {
            return Array.Empty<DocumentChange>();
        }

        var fromHeader = FindDefiningHeader(target.ProjectDir, fromClassName);
        var toHeader = FindDefiningHeader(target.ProjectDir, toClassName);
        if (fromHeader is null || toHeader is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var fromOriginal = File.ReadAllText(fromHeader);
        var fromTokens = CppCliLexer.Tokenize(fromOriginal);
        var fromDecls = CppCliDeclParser.Parse(fromTokens);
        var fromDecl = fromDecls.FirstOrDefault(d => d.Name == fromClassName);
        if (fromDecl is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var wanted = new HashSet<string>(wantedMemberNames, StringComparer.Ordinal);
        var selected = fromDecl.Members
            .Where(m => wanted.Contains(m.Name))
            .ToList();
        if (selected.Count == 0)
        {
            return Array.Empty<DocumentChange>();
        }

        var fromUpdated = RemoveMemberDeclarations(fromOriginal, selected);

        if (string.Equals(fromHeader, toHeader, StringComparison.OrdinalIgnoreCase))
        {
            var combined = AppendMembersToClassBody(fromUpdated, toClassName, selected);
            return new[]
            {
                new DocumentChange(fromHeader, DocumentChangeKind.Modified, fromOriginal, combined),
            };
        }

        var toOriginal = File.ReadAllText(toHeader);
        var toUpdated = AppendMembersToClassBody(toOriginal, toClassName, selected);

        return new[]
        {
            new DocumentChange(fromHeader, DocumentChangeKind.Modified, fromOriginal, fromUpdated),
            new DocumentChange(toHeader, DocumentChangeKind.Modified, toOriginal, toUpdated),
        };
    }

    private static string RemoveMemberDeclarations(string text, IReadOnlyList<CppMember> members)
    {
        var updated = text;
        foreach (var m in members)
        {
            // Line-scoped removal: drop the first line that declares this member.
            // Matches `[indent]... memberName( ... );` OR `[indent]... memberName;`
            // (with optional `property`/`event`/`virtual` prefix already contained in the line).
            var pattern = new Regex(
                $@"[ \t]*[^\n]*\b{Regex.Escape(m.Name)}\s*[\(;][^\n]*\r?\n",
                RegexOptions.Compiled);
            updated = pattern.Replace(updated, string.Empty, count: 1);
        }
        return updated;
    }

    public static IReadOnlyList<DocumentChange> RemoveSettingMethod(
        CppTargetProject target,
        string ownerName,
        string propertyName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(propertyName))
        {
            return Array.Empty<DocumentChange>();
        }

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var original = File.ReadAllText(headerPath);

        // Candidate names for a setter to remove.
        var setterName = propertyName.StartsWith("Set", StringComparison.Ordinal)
            ? propertyName
            : "Set" + propertyName;

        var pattern = new Regex(
            $@"[ \t]*[^\n]*\b{Regex.Escape(setterName)}\s*\([^\n]*;\s*\r?\n",
            RegexOptions.Compiled);
        var updated = pattern.Replace(original, string.Empty, count: 1);

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static IReadOnlyList<DocumentChange> PullUpConstructorBody(
        CppTargetProject target,
        string subclassName,
        string parentName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(subclassName) || !identPattern.IsMatch(parentName))
        {
            return Array.Empty<DocumentChange>();
        }

        var subHeader = FindDefiningHeader(target.ProjectDir, subclassName);
        var parentHeader = FindDefiningHeader(target.ProjectDir, parentName);
        if (subHeader is null || parentHeader is null)
        {
            return Array.Empty<DocumentChange>();
        }

        var subOriginal = File.ReadAllText(subHeader);
        var parentOriginal = File.ReadAllText(parentHeader);

        var subCtorRegex = new Regex(
            $@"(?<indent>[ \t]*){Regex.Escape(subclassName)}\s*\((?<args>[^\)]*)\)(?<init>\s*:\s*[^\{{]*)?\s*\{{(?<body>[^\}}]*)\}}",
            RegexOptions.Compiled);
        var subCtorMatch = subCtorRegex.Match(subOriginal);
        if (!subCtorMatch.Success)
        {
            return Array.Empty<DocumentChange>();
        }

        var pulledBody = subCtorMatch.Groups["body"].Value.Trim();
        if (pulledBody.Length == 0)
        {
            return Array.Empty<DocumentChange>();
        }

        var parentCtorRegex = new Regex(
            $@"(?<indent>[ \t]*){Regex.Escape(parentName)}\s*\((?<args>[^\)]*)\)(?<init>\s*:\s*[^\{{]*)?\s*\{{(?<body>[^\}}]*)\}}",
            RegexOptions.Compiled);
        var parentCtorMatch = parentCtorRegex.Match(parentOriginal);

        string parentUpdated;
        if (parentCtorMatch.Success)
        {
            var existingBody = parentCtorMatch.Groups["body"].Value.TrimEnd();
            var merged = existingBody.Length == 0
                ? "\n        " + pulledBody + "\n    "
                : existingBody + "\n        " + pulledBody + "\n    ";
            parentUpdated = parentOriginal.Substring(0, parentCtorMatch.Groups["body"].Index)
                + merged
                + parentOriginal.Substring(parentCtorMatch.Groups["body"].Index + parentCtorMatch.Groups["body"].Length);
        }
        else
        {
            // Insert a new public parameterless ctor after `class Parent {`.
            var classOpen = new Regex(
                $@"\b(class|struct)\s+{Regex.Escape(parentName)}\b[^{{]*\{{",
                RegexOptions.Compiled);
            var openMatch = classOpen.Match(parentOriginal);
            if (!openMatch.Success)
            {
                return Array.Empty<DocumentChange>();
            }
            var insertion = $"\n    public:\n        {parentName}()\n        {{\n            {pulledBody}\n        }}";
            parentUpdated = parentOriginal.Substring(0, openMatch.Index + openMatch.Length)
                + insertion
                + parentOriginal.Substring(openMatch.Index + openMatch.Length);
        }

        // Replace sub ctor body with empty; ensure `: Parent()` init.
        var subCtorFull = subCtorMatch.Value;
        var hasInit = subCtorMatch.Groups["init"].Success && subCtorMatch.Groups["init"].Value.Trim().Length > 0;
        var args = subCtorMatch.Groups["args"].Value;
        var indent = subCtorMatch.Groups["indent"].Value;
        var newInit = hasInit ? subCtorMatch.Groups["init"].Value : $" : {parentName}()";
        var newSubCtor = $"{indent}{subclassName}({args}){newInit} {{ }}";
        var subUpdated = subOriginal.Substring(0, subCtorMatch.Index)
            + newSubCtor
            + subOriginal.Substring(subCtorMatch.Index + subCtorFull.Length);

        var changes = new List<DocumentChange>();
        if (!string.Equals(parentOriginal, parentUpdated, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(parentHeader, DocumentChangeKind.Modified, parentOriginal, parentUpdated));
        }
        if (!string.Equals(subOriginal, subUpdated, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(subHeader, DocumentChangeKind.Modified, subOriginal, subUpdated));
        }
        return changes;
    }

    public static IReadOnlyList<DocumentChange> EncapsulateField(
        CppTargetProject target,
        string ownerName,
        string fieldName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(fieldName))
        {
            return Array.Empty<DocumentChange>();
        }

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        // Match `[indent]<type> <fieldName>;` and rewrite to `[indent]property <type> <fieldName>;`.
        // Type token can include `^`, `<`, `>`, `::`, spaces (e.g. `System::String^`, `array<int>^`).
        var pattern = new Regex(
            $@"^(?<indent>[ \t]*)(?<type>[A-Za-z_][^;\n]*?)\s+{Regex.Escape(fieldName)}\s*;[ \t]*$",
            RegexOptions.Compiled | RegexOptions.Multiline);
        var m = pattern.Match(original);
        if (!m.Success) return Array.Empty<DocumentChange>();

        var typeToken = m.Groups["type"].Value.Trim();
        if (typeToken.Length == 0
            || typeToken.Equals("property", StringComparison.Ordinal)
            || typeToken.EndsWith(" property", StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        var indent = m.Groups["indent"].Value;
        var replacement = $"{indent}property {typeToken} {fieldName};";
        var updated = original.Substring(0, m.Index) + replacement + original.Substring(m.Index + m.Length);
        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static IReadOnlyList<DocumentChange> ReplaceConstructorWithFactory(
        CppTargetProject target,
        string ownerName,
        string factoryName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(factoryName))
        {
            return Array.Empty<DocumentChange>();
        }

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        // Detect the first constructor `Owner(...)` inside the class body.
        var ctorRegex = new Regex(
            $@"(?<indent>[ \t]*){Regex.Escape(ownerName)}\s*\((?<args>[^\)]*)\)",
            RegexOptions.Compiled);
        var ctorMatch = ctorRegex.Match(original);
        if (!ctorMatch.Success) return Array.Empty<DocumentChange>();

        var indent = ctorMatch.Groups["indent"].Value;
        var args = ctorMatch.Groups["args"].Value;
        var argNames = SplitParameterNames(args);
        var argCall = string.Join(", ", argNames);

        var factoryDecl = $"\n{indent}static {ownerName}^ {factoryName}({args}) {{ return gcnew {ownerName}({argCall}); }}";

        // Insert factory right after the constructor's closing brace (or semicolon for declarations).
        // Simplest approach: append after the matched ctor signature, on the same indent.
        // We insert right after the class body's opening `{` for safety.
        var classOpen = new Regex(
            $@"\b(class|struct)\s+{Regex.Escape(ownerName)}\b[^{{]*\{{",
            RegexOptions.Compiled);
        var openMatch = classOpen.Match(original);
        if (!openMatch.Success) return Array.Empty<DocumentChange>();

        var insertion = $"\n{indent}public:{factoryDecl}";
        var updated = original.Substring(0, openMatch.Index + openMatch.Length)
            + insertion
            + original.Substring(openMatch.Index + openMatch.Length);

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    private static IReadOnlyList<string> SplitParameterNames(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var raw in args.Split(','))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var lastSpace = trimmed.LastIndexOf(' ');
            result.Add(lastSpace < 0 ? trimmed : trimmed[(lastSpace + 1)..]);
        }
        return result;
    }

    public static IReadOnlyList<DocumentChange> ReplaceMagicNumber(
        CppTargetProject target,
        string ownerName,
        string literalValue,
        string constantName,
        string constantType)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(constantName))
        {
            return Array.Empty<DocumentChange>();
        }
        if (string.IsNullOrEmpty(literalValue)) return Array.Empty<DocumentChange>();

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        // Insert `static const T Name = value;` at the top of the class body.
        var classOpen = new Regex(
            $@"\b(class|struct)\s+{Regex.Escape(ownerName)}\b[^{{]*\{{",
            RegexOptions.Compiled);
        var openMatch = classOpen.Match(original);
        if (!openMatch.Success) return Array.Empty<DocumentChange>();

        var insertion = $"\n    public: static const {constantType} {constantName} = {literalValue};";
        var withConst = original.Substring(0, openMatch.Index + openMatch.Length)
            + insertion
            + original.Substring(openMatch.Index + openMatch.Length);

        // Replace occurrences of the literal within the class body.
        // Boundary-safe: use a regex where the literal is not preceded/followed by identifier chars.
        var replaceRegex = new Regex(
            $@"(?<![A-Za-z0-9_\.]){Regex.Escape(literalValue)}(?![A-Za-z0-9_\.])",
            RegexOptions.Compiled);
        // Skip the insertion itself (which contains the literal on the const-line).
        var boundary = openMatch.Index + openMatch.Length + insertion.Length;
        var before = withConst.Substring(0, boundary);
        var after = withConst.Substring(boundary);
        var afterReplaced = replaceRegex.Replace(after, constantName);
        var updated = before + afterReplaced;

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static IReadOnlyList<DocumentChange> RemoveFieldFromClass(
        CppTargetProject target,
        string ownerName,
        string fieldName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(fieldName))
        {
            return Array.Empty<DocumentChange>();
        }

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        // Match a whole line of the form `[indent]<type-tokens> <fieldName>;` (with optional
        // trailing whitespace) and drop it. Also tolerate the `property` prefix.
        var pattern = new Regex(
            $@"^[ \t]*(property\s+)?[A-Za-z_][^;\n]*?\s+{Regex.Escape(fieldName)}\s*;[ \t]*\r?\n",
            RegexOptions.Compiled | RegexOptions.Multiline);
        var updated = pattern.Replace(original, string.Empty, count: 1);
        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static IReadOnlyList<DocumentChange> AddParameterToMethod(
        CppTargetProject target,
        string ownerName,
        string methodName,
        string parameterDeclaration)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(methodName))
        {
            return Array.Empty<DocumentChange>();
        }
        if (string.IsNullOrWhiteSpace(parameterDeclaration)) return Array.Empty<DocumentChange>();

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        // Match `methodName(<args>)` and rewrite the args, appending the new declaration.
        var pattern = new Regex(
            $@"\b{Regex.Escape(methodName)}\s*\((?<args>[^\)]*)\)",
            RegexOptions.Compiled);
        var m = pattern.Match(original);
        if (!m.Success) return Array.Empty<DocumentChange>();

        var args = m.Groups["args"].Value;
        var trimmed = args.TrimEnd();
        var separator = trimmed.Length == 0 ? "" : ", ";
        var newArgs = trimmed + separator + parameterDeclaration;

        var argsGroup = m.Groups["args"];
        var updated = original.Substring(0, argsGroup.Index)
            + newArgs
            + original.Substring(argsGroup.Index + argsGroup.Length);

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }
        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static IReadOnlyList<DocumentChange> RemoveParameterFromMethod(
        CppTargetProject target,
        string ownerName,
        string methodName,
        string parameterName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(methodName) || !identPattern.IsMatch(parameterName))
        {
            return Array.Empty<DocumentChange>();
        }

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        var pattern = new Regex(
            $@"\b{Regex.Escape(methodName)}\s*\((?<args>[^\)]*)\)",
            RegexOptions.Compiled);
        var m = pattern.Match(original);
        if (!m.Success) return Array.Empty<DocumentChange>();

        var argsGroup = m.Groups["args"];
        var args = argsGroup.Value;
        var pieces = args.Split(',');
        var kept = new List<string>();
        foreach (var raw in pieces)
        {
            var piece = raw.Trim();
            if (piece.Length == 0) continue;
            // Extract identifier after the last whitespace.
            var lastSpace = piece.LastIndexOf(' ');
            var name = lastSpace < 0 ? piece : piece[(lastSpace + 1)..];
            if (string.Equals(name, parameterName, StringComparison.Ordinal)) continue;
            kept.Add(piece);
        }
        var newArgs = string.Join(", ", kept);
        var updated = original.Substring(0, argsGroup.Index)
            + newArgs
            + original.Substring(argsGroup.Index + argsGroup.Length);

        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }
        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static IReadOnlyList<DocumentChange> RenameParameter(
        CppTargetProject target,
        string ownerName,
        string methodName,
        string oldParamName,
        string newParamName)
    {
        var identPattern = new Regex(@"\A[A-Za-z0-9_]+\z");
        if (!identPattern.IsMatch(ownerName) || !identPattern.IsMatch(methodName)
            || !identPattern.IsMatch(oldParamName) || !identPattern.IsMatch(newParamName))
        {
            return Array.Empty<DocumentChange>();
        }

        var headerPath = FindDefiningHeader(target.ProjectDir, ownerName);
        if (headerPath is null) return Array.Empty<DocumentChange>();

        var original = File.ReadAllText(headerPath);

        // Match `methodName(args)` first, then rewrite `oldParamName` (word boundary)
        // ONLY inside the args group. Body rewrite is caller's problem.
        var pattern = new Regex(
            $@"\b{Regex.Escape(methodName)}\s*\((?<args>[^\)]*)\)",
            RegexOptions.Compiled);
        var m = pattern.Match(original);
        if (!m.Success) return Array.Empty<DocumentChange>();

        var argsGroup = m.Groups["args"];
        var oldArgs = argsGroup.Value;
        var wordBoundary = new Regex($@"\b{Regex.Escape(oldParamName)}\b", RegexOptions.Compiled);
        var newArgs = wordBoundary.Replace(oldArgs, newParamName);
        if (string.Equals(oldArgs, newArgs, StringComparison.Ordinal))
        {
            return Array.Empty<DocumentChange>();
        }

        var updated = original.Substring(0, argsGroup.Index)
            + newArgs
            + original.Substring(argsGroup.Index + argsGroup.Length);

        return new[]
        {
            new DocumentChange(headerPath, DocumentChangeKind.Modified, original, updated),
        };
    }

    public static bool TryFindTargetByType(
        SolutionModel model,
        TypeRef typeRef,
        out CppTargetProject? target)
    {
        target = null;
        foreach (var p in model.Projects)
        {
            if (!string.Equals(p.LanguageId, "cpp-cli", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (p.Types.Any(t => t.Ref.FullyQualifiedName == typeRef.FullyQualifiedName))
            {
                var dir = Path.GetDirectoryName(p.FilePath);
                if (dir is null) return false;
                target = new CppTargetProject(p.FilePath, dir, p.Name);
                return true;
            }
        }
        return false;
    }
}
