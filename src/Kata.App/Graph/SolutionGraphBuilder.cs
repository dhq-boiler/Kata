using System.Collections.Generic;
using System.Linq;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Graph;

public sealed record BuiltGraph(
    IReadOnlyList<TypeNodeViewModel> Nodes,
    IReadOnlyList<ConnectionViewModel> Connections);

public static class SolutionGraphBuilder
{
    public static BuiltGraph Build(SolutionModel model)
    {
        var nodes = new List<TypeNodeViewModel>();
        var byRef = new Dictionary<TypeRef, TypeNodeViewModel>();

        foreach (var project in model.Projects)
        {
            foreach (var type in project.Types)
            {
                var vm = new TypeNodeViewModel(type);
                nodes.Add(vm);
                byRef[type.Ref] = vm;
            }
        }

        var externalRefs = new Dictionary<TypeRef, TypeNodeViewModel>();
        foreach (var project in model.Projects)
        {
            foreach (var type in project.Types)
            {
                foreach (var referenced in type.BaseTypes.Concat(type.ImplementedInterfaces))
                {
                    if (byRef.ContainsKey(referenced) || externalRefs.ContainsKey(referenced))
                    {
                        continue;
                    }

                    var (name, ns) = SplitTypeRef(referenced.FullyQualifiedName);
                    var vm = new TypeNodeViewModel(referenced, name, ns);
                    nodes.Add(vm);
                    externalRefs[referenced] = vm;
                }
            }
        }

        foreach (var (@ref, vm) in externalRefs)
        {
            byRef[@ref] = vm;
        }

        // Short-name → node index for Uses edge lookup. Multiple types may share a
        // simple name; we resolve all of them (rare, mostly for cross-namespace collisions).
        var byShortName = new Dictionary<string, List<TypeNodeViewModel>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var (name, _) = SplitTypeRef(node.Ref.FullyQualifiedName);
            if (!byShortName.TryGetValue(name, out var list))
            {
                list = new List<TypeNodeViewModel>();
                byShortName[name] = list;
            }
            list.Add(node);

            // File-scope pseudo types get an EXTRA key for their fileName (e.g. "pch.h").
            // Their FQN is "vcxproj.pch.h", SplitTypeRef strips the last dot and yields "h",
            // which isn't the identifier body scans will look up. Register "pch.h" so
            // BodyReferencedTypeNames entries produced by macro/function-name promotion
            // can resolve here.
            if (LooksLikeFileScopePseudoType(node.Name) && !string.Equals(node.Name, name, System.StringComparison.Ordinal))
            {
                if (!byShortName.TryGetValue(node.Name, out var listByFile))
                {
                    listByFile = new List<TypeNodeViewModel>();
                    byShortName[node.Name] = listByFile;
                }
                listByFile.Add(node);
            }
        }

        var connections = new List<ConnectionViewModel>();
        // Deduplicate Uses edges: at most one Uses edge per (source, target).
        var usesPairs = new HashSet<(TypeNodeViewModel Src, TypeNodeViewModel Dst)>();

        foreach (var project in model.Projects)
        {
            foreach (var type in project.Types)
            {
                if (!byRef.TryGetValue(type.Ref, out var source))
                {
                    continue;
                }

                foreach (var baseTypeRef in type.BaseTypes)
                {
                    if (byRef.TryGetValue(baseTypeRef, out var target))
                    {
                        connections.Add(new ConnectionViewModel(sourceNode: source, targetNode: target, ConnectionKind.Inheritance));
                    }
                }

                foreach (var ifaceRef in type.ImplementedInterfaces)
                {
                    if (byRef.TryGetValue(ifaceRef, out var target))
                    {
                        connections.Add(new ConnectionViewModel(sourceNode: source, targetNode: target, ConnectionKind.Interface));
                    }
                }

                // Uses edges: any type name referenced by a member's return type or parameter list
                // that matches a known node (and isn't the type itself).
                foreach (var member in type.Members)
                {
                    foreach (var typeName in ExtractReferencedTypeNames(member))
                    {
                        if (!byShortName.TryGetValue(typeName, out var candidates)) continue;
                        foreach (var target in candidates)
                        {
                            if (ReferenceEquals(source, target)) continue;
                            if (usesPairs.Add((source, target)))
                            {
                                connections.Add(new ConnectionViewModel(sourceNode: source, targetNode: target, ConnectionKind.Uses));
                            }
                        }
                    }
                }

                // TypeModel.BodyReferencedTypeNames (adapter が member 本体を走査して
                // 拾った他型の短名リスト) からも uses エッジを引く。Extract Method で
                // 切り出した helper への矢印がここで初めて出る。null なら noop。
                if (type.BodyReferencedTypeNames is { Count: > 0 } bodyRefs)
                {
                    foreach (var typeName in bodyRefs)
                    {
                        if (!byShortName.TryGetValue(typeName, out var candidates)) continue;
                        foreach (var target in candidates)
                        {
                            if (ReferenceEquals(source, target)) continue;
                            if (usesPairs.Add((source, target)))
                            {
                                connections.Add(new ConnectionViewModel(sourceNode: source, targetNode: target, ConnectionKind.Uses));
                            }
                        }
                    }
                }
            }
        }

        // Companion edges: 擬似 file-scope type "MockAudioDestination.cpp" ↔ 実 class "MockAudioDestination"
        // (同 Namespace 内で拡張子違いでペアになるもの) を Uses エッジで結んで、
        // Impact Focus の BFS で片方 seed から他方に到達できるようにする。
        // Uses なので普段の view では非表示、Impact Focus 時のみ辿られる (レイアウトにも影響しない)。
        foreach (var project in model.Projects)
        {
            foreach (var type in project.Types)
            {
                if (!LooksLikeFileScopePseudoType(type.Name)) continue;
                var stem = System.IO.Path.GetFileNameWithoutExtension(type.Name);
                var realFqn = string.IsNullOrEmpty(type.Namespace.FullName) ? stem : $"{type.Namespace.FullName}.{stem}";
                if (!byRef.TryGetValue(type.Ref, out var pseudoNode)) continue;
                if (!byRef.TryGetValue(new TypeRef(realFqn), out var realNode)) continue;
                if (usesPairs.Add((realNode, pseudoNode)))
                {
                    connections.Add(new ConnectionViewModel(realNode, pseudoNode, ConnectionKind.Uses));
                }
            }
        }

        return new BuiltGraph(nodes, connections);
    }

    // Cpp/CLI implementation / header ファイル名パターン (BuildFileScopePseudoTypes が付ける Name)
    private static bool LooksLikeFileScopePseudoType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        var ext = System.IO.Path.GetExtension(typeName);
        return ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hh", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractReferencedTypeNames(MemberModel member)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ExtractIdentifiersFromTypeText(member.ReturnTypeDisplay))
        {
            if (seen.Add(name)) yield return name;
        }
        // Parse the parameter list out of the signature: "Ret Name(Type1 name1, Type2 name2)"
        var sig = member.Ref.Signature;
        int open = sig.IndexOf('(');
        int close = sig.LastIndexOf(')');
        if (open < 0 || close <= open) yield break;
        var inner = sig.Substring(open + 1, close - open - 1);
        if (string.IsNullOrWhiteSpace(inner)) yield break;

        int depth = 0;
        int start = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            char c = i < inner.Length ? inner[i] : ',';
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                var arg = inner.Substring(start, i - start).Trim();
                var typePart = SplitParamTypeAndName(arg);
                foreach (var name in ExtractIdentifiersFromTypeText(typePart))
                {
                    if (seen.Add(name)) yield return name;
                }
                start = i + 1;
            }
        }
    }

    private static string SplitParamTypeAndName(string arg)
    {
        // "ISource source" → "ISource". "int" (no name) → "int".
        arg = arg.Trim();
        if (arg.Length == 0) return string.Empty;
        int lastSpace = -1;
        int depth = 0;
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if ((c == ' ' || c == '\t') && depth == 0) lastSpace = i;
        }
        return lastSpace < 0 ? arg : arg.Substring(0, lastSpace).TrimEnd();
    }

    private static IEnumerable<string> ExtractIdentifiersFromTypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        // Emit each identifier-looking token from the type expression.
        // Handles nested generics like "IReadOnlyList<ConnectionHandle>" → ["IReadOnlyList", "ConnectionHandle"].
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text.Substring(start, i - start);
                if (!IsBuiltinTypeName(word)) yield return word;
            }
            else
            {
                i++;
            }
        }
    }

    private static bool IsBuiltinTypeName(string name) => name switch
    {
        "void" or "bool" or "byte" or "sbyte" or "char" or "short" or "ushort"
            or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal"
            or "object" or "string" or "dynamic" or "nint" or "nuint"
            or "System" or "var" or "auto" or "const" or "static"
            or "true" or "false" or "null" or "this" or "base" => true,
        _ => false,
    };

    private static (string Name, NamespaceRef Namespace) SplitTypeRef(string fullyQualifiedName)
    {
        var depth = 0;
        var lastDot = -1;
        for (var i = 0; i < fullyQualifiedName.Length; i++)
        {
            var c = fullyQualifiedName[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == '.' && depth == 0) lastDot = i;
        }

        if (lastDot < 0)
        {
            return (fullyQualifiedName, NamespaceRef.Global);
        }

        var name = fullyQualifiedName.Substring(lastDot + 1);
        var ns = fullyQualifiedName.Substring(0, lastDot);
        return (name, new NamespaceRef(ns));
    }
}
