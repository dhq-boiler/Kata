using System.Text;
using Kata.Core;
using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Core.Sln;
using Kata.Cpp;
using Kata.Cpp.Bridge;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;
using Kata.Roslyn.HybridResolution;
using Kata.Roslyn.ModelBuilding;
using Kata.Roslyn.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using MsSolution = Microsoft.CodeAnalysis.Solution;
using RoslynReferenceLocation = Microsoft.CodeAnalysis.FindSymbols.ReferenceLocation;
using ReferenceLocation = Kata.Core.Model.ReferenceLocation;

namespace Kata.Roslyn;

public sealed class CSharpLanguageAdapter : ILanguageAdapter, IDisposable
{
    private MSBuildWorkspace? _workspace;
    private MsSolution? _solution;
    private CppCompilation? _cppCompilation;
    private readonly HashSet<string> _injectedCppAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _staleCppShimWarnings = new();

    // Cache of foreign (C++/CLI etc.) projects last enumerated in LoadSolutionAsync.
    // C# refactors never touch these on disk, so ApplyChangesAsync should preserve them
    // rather than let SolutionDiffer flag every foreign type as removed.
    // Cpp refactors DO touch them — ApplyChangesAsync rebuilds via _lastDiscoveredProjects.
    private IReadOnlyList<ProjectModel> _lastForeignProjects = Array.Empty<ProjectModel>();

    // Discovery snapshot from LoadSolutionAsync. Kept so ApplyChangesAsync can rebuild
    // _cppCompilation + _lastForeignProjects incrementally when a change touches a Cpp
    // header / impl file (otherwise the diagram, smell index, and Ctrl+Click index all
    // remain frozen at the pre-apply state — "diff not reflected" symptom).
    private IReadOnlyList<DiscoveredProject> _lastDiscoveredForeignProjects = Array.Empty<DiscoveredProject>();

    /// <summary>
    /// Warnings emitted during LoadSolutionAsync about Cpp shim DLLs that were
    /// injected despite being older than their sources. Non-empty means the C#
    /// side may see the old symbol names — user should rebuild the C++/CLI project
    /// for accurate cross-language resolution.
    /// </summary>
    public IReadOnlyList<string> StaleCppShimWarnings => _staleCppShimWarnings;

    public string LanguageId => "csharp";

    public IReadOnlyCollection<Type> SupportedIntentTypes { get; } = new[]
    {
        typeof(RenameIntent),
        typeof(ExtractInterfaceIntent),
        typeof(ExtractSuperclassIntent),
        typeof(ExtractClassIntent),
        typeof(RemoveSubclassIntent),
        typeof(CollapseHierarchyIntent),
        typeof(PullUpMethodIntent),
        typeof(PushDownMethodIntent),
        typeof(PullUpFieldIntent),
        typeof(PushDownFieldIntent),
        typeof(RemoveSettingMethodIntent),
        typeof(RenameFieldIntent),
        typeof(PullUpConstructorBodyIntent),
        typeof(EncapsulateFieldIntent),
        typeof(MoveMethodIntent),
        typeof(MoveFieldIntent),
        typeof(ReplaceConstructorWithFactoryIntent),
        typeof(ReplaceMagicNumberIntent),
        typeof(ChangeBidirectionalToUnidirectionalIntent),
        typeof(IntroduceParameterObjectIntent),
        typeof(AddParameterIntent),
        typeof(RemoveParameterIntent),
        typeof(ReplaceDataValueWithObjectIntent),
        typeof(RenameParameterIntent),
        typeof(SelfEncapsulateFieldIntent),
        typeof(ChangeReferenceToValueIntent),
        typeof(ChangeValueToReferenceIntent),
        typeof(ReplaceTypeCodeWithClassIntent),
        typeof(PreserveWholeObjectIntent),
        typeof(ReplaceArrayWithObjectIntent),
        typeof(ReplaceTypeCodeWithSubclassesIntent),
        typeof(ExtractHierarchyIntent),
        typeof(TeaseApartInheritanceIntent),
        typeof(ConvertProceduralToObjectsIntent),
        typeof(ExtractMethodIntent),
        typeof(ExtractVariableIntent),
        typeof(InlineMethodIntent),
        typeof(InlineVariableIntent),
        typeof(DecomposeConditionalIntent),
        typeof(ConsolidateConditionalExpressionIntent),
        typeof(ConsolidateDuplicateConditionalFragmentsIntent),
        typeof(ReplaceNestedConditionalWithGuardClausesIntent),
        typeof(IntroduceNullObjectIntent),
        typeof(IntroduceAssertionIntent),
        typeof(ReplaceSubclassWithFieldsIntent),
        typeof(AddGhostTypeIntent),
    };

    public async Task<MemberSource?> GetMemberSourceAsync(
        SolutionModel model,
        TypeRef ownerType,
        MemberRef member,
        CancellationToken cancellationToken = default)
    {
        if (_solution is null)
        {
            return null;
        }

        // File-level function synthetic sentinel: navigate to the function's declaration
        // inside a Cpp implementation file.
        if (ownerType.FullyQualifiedName.StartsWith(CppContextClickResolver.FileFunctionOwnerPrefix, StringComparison.Ordinal)
            && _cppCompilation is not null)
        {
            return TryGetCppFileFunctionSource(ownerType, member);
        }

        var ownerSymbol = await SymbolResolver.ResolveAsync(_solution, ownerType, null, cancellationToken)
            .ConfigureAwait(false) as INamedTypeSymbol;

        // Sentinel signature = "the type itself, not a member". Try Roslyn first (source-defined C# type),
        // then fall through to Cpp compilation.
        if (string.Equals(member.Signature, CppTypeSiteSignature, StringComparison.Ordinal))
        {
            if (ownerSymbol is not null)
            {
                var csharpTypeSource = await BuildCsharpTypeSiteMemberSourceAsync(
                    ownerSymbol, ownerType, member, cancellationToken).ConfigureAwait(false);
                if (csharpTypeSource is not null)
                {
                    return csharpTypeSource;
                }
            }
            return TryGetCppMemberSource(ownerType, member);
        }

        if (ownerSymbol is null)
        {
            return TryGetCppMemberSource(ownerType, member);
        }

        var matched = ownerSymbol.GetMembers()
            .Where(s => s is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            .FirstOrDefault(s => RoslynToModelMapper.ToMemberRef(s).Signature == member.Signature);
        if (matched is null)
        {
            return TryGetCppMemberSource(ownerType, member);
        }

        var sref = matched.DeclaringSyntaxReferences.FirstOrDefault();
        if (sref is null)
        {
            return null;
        }

        var doc = _solution.GetDocument(sref.SyntaxTree);
        if (doc is null || doc.FilePath is null)
        {
            return null;
        }

        var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

        var memberNode = node.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? node;
        var memberSpan = memberNode.FullSpan;

        var (bodyStart, bodyLength) = GetBodySpan(node);

        return new MemberSource(
            OwnerType: ownerType,
            Member: member,
            FilePath: doc.FilePath,
            SourceText: text.GetSubText(memberSpan).ToString(),
            MemberSpanStart: memberSpan.Start,
            MemberSpanLength: memberSpan.Length,
            BodySpanStart: bodyStart - memberSpan.Start,
            BodySpanLength: bodyLength);
    }

    public string? LastResolveDiagnostic { get; private set; }

    public async Task<(TypeRef OwnerType, MemberRef Member)?> ResolveMemberAtAsync(
        SolutionModel model,
        TypeRef contextOwnerType,
        MemberRef contextMember,
        int offsetInSource,
        CancellationToken cancellationToken = default)
    {
        LastResolveDiagnostic = null;
        if (_solution is null) { LastResolveDiagnostic = "solution not loaded"; return null; }

        var ownerSymbol = await SymbolResolver.ResolveAsync(_solution, contextOwnerType, null, cancellationToken)
            .ConfigureAwait(false) as INamedTypeSymbol;
        if (ownerSymbol is null)
        {
            // Cpp-view context: the host member is a Cpp type living in _cppCompilation,
            // not in the Roslyn Solution. Resolve tokens directly against the Cpp tree.
            if (_cppCompilation is null)
            {
                LastResolveDiagnostic = $"host type not found: {contextOwnerType.FullyQualifiedName} [cpp=off]";
                return null;
            }

            var cppResult = CppContextClickResolver.TryResolve(
                _cppCompilation,
                contextOwnerType,
                contextMember,
                offsetInSource,
                CppTypeSiteSignature,
                out var cppReason);
            if (cppResult is { } r)
            {
                LastResolveDiagnostic = r.DiagnosticSummary;
                return (r.OwnerType, r.Member);
            }

            LastResolveDiagnostic = $"host type not found: {contextOwnerType.FullyQualifiedName} [{cppReason}]";
            return null;
        }

        var matched = ownerSymbol.GetMembers()
            .Where(s => s is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            .FirstOrDefault(s => RoslynToModelMapper.ToMemberRef(s).Signature == contextMember.Signature);
        if (matched is null) { LastResolveDiagnostic = $"host member not found: {contextMember.Signature}"; return null; }

        var sref = matched.DeclaringSyntaxReferences.FirstOrDefault();
        if (sref is null) { LastResolveDiagnostic = "host member has no source"; return null; }

        var srefTree = sref.SyntaxTree;
        var doc = _solution.GetDocument(srefTree);
        if (doc is null) { LastResolveDiagnostic = "host document unavailable"; return null; }

        var compilation = await doc.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null) { LastResolveDiagnostic = "compilation unavailable"; return null; }

        SemanticModel semantic;
        try
        {
            semantic = compilation.GetSemanticModel(srefTree);
        }
        catch (ArgumentException)
        {
            semantic = (await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false))!;
        }
        if (semantic is null) { LastResolveDiagnostic = "semantic model unavailable"; return null; }

        var srefNode = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
        var memberNode = srefNode.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? srefNode;
        var syntaxRoot = memberNode.SyntaxTree.GetRoot(cancellationToken);
        var absolutePosition = memberNode.FullSpan.Start + offsetInSource;
        if (absolutePosition < 0 || absolutePosition > syntaxRoot.FullSpan.End)
        {
            LastResolveDiagnostic = $"absolute position out of range: {absolutePosition}";
            return null;
        }

        var token = syntaxRoot.FindToken(absolutePosition);

        var symbol = ResolveSymbolNearToken(token, semantic, cancellationToken);
        if (symbol is null)
        {
            var hybrid = TryHybridResolveFromToken(token, semantic);
            if (hybrid is not null)
            {
                var cppOwnerRef = new TypeRef(hybrid.Type.FullyQualifiedName);
                if (hybrid.PreferTypeSite)
                {
                    LastResolveDiagnostic = $"Ctrl+Click via Kata.Cpp [type]: {hybrid.Type.FullyQualifiedName}";
                    return (cppOwnerRef, new MemberRef(cppOwnerRef, CppTypeSiteSignature));
                }

                var siteKind = hybrid.Member.ImplementationSite is not null ? "impl" : "decl";
                LastResolveDiagnostic = $"Ctrl+Click via Kata.Cpp [{siteKind}]: {hybrid.Type.FullyQualifiedName}.{hybrid.Member.Name}";
                return (cppOwnerRef, new MemberRef(cppOwnerRef, hybrid.Member.Signature));
            }

            // Second fallback: C# named type — Roslyn resolves it but ResolveSymbolNearToken
            // filters out INamedTypeSymbol unless the click landed on a `new X()` receiver.
            // Recover source-defined named types here so type-name Ctrl+Click reaches .cs.
            var csharpType = TryResolveCsharpNamedTypeFromToken(token, semantic, cancellationToken);
            if (csharpType is not null)
            {
                var typeRef = RoslynToModelMapper.ToTypeRef(csharpType);
                LastResolveDiagnostic = $"Ctrl+Click via Roslyn [type]: {typeRef.FullyQualifiedName}";
                return (typeRef, new MemberRef(typeRef, CppTypeSiteSignature));
            }

            var reason = DescribeResolveFailure(token, semantic, cancellationToken);
            var cppState = _cppCompilation is null
                ? "cpp=off"
                : $"cpp={_cppCompilation.AllTypes.Count}t/{_cppCompilation.ImplementationTrees.Count}cpp";
            LastResolveDiagnostic = $"no symbol at token '{token.ValueText}' — {reason} [{cppState}]";
            return null;
        }

        var originalKind = symbol.Kind;
        if (symbol is IMethodSymbol methodSymbol)
        {
            symbol = (methodSymbol.ReducedFrom ?? methodSymbol).OriginalDefinition;
        }
        else
        {
            symbol = symbol.OriginalDefinition;
        }

        if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
        {
            LastResolveDiagnostic = $"not navigable: {originalKind} ({symbol})";
            return null;
        }

        var containingType = symbol.ContainingType;
        if (containingType is null) { LastResolveDiagnostic = "target has no containing type"; return null; }

        // Injected Cpp DLL: Roslyn resolved the symbol from metadata, but the source lives
        // in a C++/CLI project we indexed as CppCompilation. Reverse-lookup by FQN + name.
        if (IsFromInjectedCppAssembly(containingType))
        {
            var reverse = TryReverseLookupCppSymbol(containingType, symbol);
            if (reverse is not null)
            {
                LastResolveDiagnostic = $"Ctrl+Click via Roslyn+metadata→Cpp: {reverse.Value.OwnerType.FullyQualifiedName}.{reverse.Value.Member.Signature}";
                return reverse;
            }
            LastResolveDiagnostic = $"Roslyn resolved metadata symbol from injected Cpp DLL, but reverse lookup missed: {containingType}.{symbol.Name}";
            return null;
        }

        if (symbol.DeclaringSyntaxReferences.Length == 0)
        {
            LastResolveDiagnostic = $"target defined outside source: {symbol}";
            return null;
        }

        if (containingType.DeclaringSyntaxReferences.Length == 0)
        {
            LastResolveDiagnostic = $"containing type defined outside source: {containingType}";
            return null;
        }

        var targetType = RoslynToModelMapper.ToTypeRef(containingType);
        var targetMember = RoslynToModelMapper.ToMemberRef(symbol);
        LastResolveDiagnostic = $"Ctrl+Click via Roslyn: {targetType.FullyQualifiedName}.{targetMember.Signature}";
        return (targetType, targetMember);
    }

    public async Task<IReadOnlyList<ReferenceLocation>> FindReferencesAsync(
        SolutionModel model,
        TypeRef ownerType,
        MemberRef? member,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ReferenceLocation>();
        if (_solution is null) return results;

        // Fan-out: C# side (Roslyn SymbolFinder) and Cpp side (name-based scan) are
        // completely independent — no data flows between them. Diamond: run both in
        // parallel, then merge + dedup + sort.
        var csTask = FindReferencesCSharpAsync(ownerType, member, cancellationToken);
        var cppTask = Task.Run(() => FindReferencesCppSide(ownerType, member), cancellationToken);

        await Task.WhenAll(csTask, cppTask).ConfigureAwait(false);
        results.AddRange(csTask.Result);
        results.AddRange(cppTask.Result);

        return DedupSort(results);
    }

    private async Task<List<ReferenceLocation>> FindReferencesCSharpAsync(
        TypeRef ownerType, MemberRef? member, CancellationToken cancellationToken)
    {
        var results = new List<ReferenceLocation>();
        var target = await ResolveTargetAcrossReferences(ownerType, member, cancellationToken).ConfigureAwait(false);
        if (target is null) return results;

        var refs = await SymbolFinder.FindReferencesAsync(target, _solution!, cancellationToken).ConfigureAwait(false);
        foreach (var r in refs)
        {
            foreach (var def in r.Definition.Locations)
            {
                if (def.IsInSource && def.SourceTree is { } tree)
                {
                    results.Add(ToReferenceLocation(tree, def.SourceSpan, ReferenceKind.Declaration, ReferenceLanguage.CSharp));
                }
            }
            foreach (var loc in r.Locations)
            {
                if (loc.Location.SourceTree is { } tree)
                {
                    var kind = member is null ? ReferenceKind.TypeUse : ReferenceKind.MemberAccess;
                    results.Add(ToReferenceLocation(tree, loc.Location.SourceSpan, kind, ReferenceLanguage.CSharp));
                }
            }
        }
        return results;
    }

    private List<ReferenceLocation> FindReferencesCppSide(TypeRef ownerType, MemberRef? member)
    {
        var results = new List<ReferenceLocation>();
        if (_cppCompilation is null) return results;

        var cppType = _cppCompilation.GetTypeByFullyQualifiedName(ownerType.FullyQualifiedName);
        if (cppType is null) return results;

        if (member is null)
        {
            foreach (var cppRef in CppReferenceFinder.FindTypeReferences(_cppCompilation, cppType))
            {
                results.Add(ToReferenceLocation(cppRef));
            }
        }
        else
        {
            var info = _cppCompilation.ResolveMember(cppType, ExtractSimpleName(member.Value.Signature));
            var cppMember = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (cppMember is not null)
            {
                foreach (var cppRef in CppReferenceFinder.FindMemberReferences(_cppCompilation, cppMember))
                {
                    results.Add(ToReferenceLocation(cppRef));
                }
            }
        }
        return results;
    }

    private async Task<ISymbol?> ResolveTargetAcrossReferences(
        TypeRef ownerType, MemberRef? member, CancellationToken cancellationToken)
    {
        // First: source-defined symbol via the standard resolver.
        var direct = await SymbolResolver.ResolveAsync(_solution!, ownerType, member, cancellationToken).ConfigureAwait(false);
        if (direct is not null) return direct;

        // Then: search each C# project's Compilation for the type, including metadata references
        // (Phase 4 injects the Cpp DLL as MetadataReference).
        var attempts = new List<string>();
        foreach (var project in _solution!.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null) { attempts.Add($"{project.Name}: no compilation"); continue; }

            // GetTypeByMetadataName is the canonical way to look up a type by FQN;
            // it searches this compilation's assembly + all metadata references.
            var typeSymbol = compilation.GetTypeByMetadataName(ownerType.FullyQualifiedName)
                          ?? FindTypeInGlobalNamespace(compilation.GlobalNamespace, ownerType.FullyQualifiedName);
            if (typeSymbol is null)
            {
                attempts.Add($"{project.Name}: type not found");
                continue;
            }
            if (member is null) return typeSymbol;

            var targetName = ExtractSimpleName(member.Value.Signature);
            // Try full signature match first.
            foreach (var m in typeSymbol.GetMembers())
            {
                if (RoslynToModelMapper.ToMemberRef(m).Signature == member.Value.Signature) return m;
            }
            // Then arity-aware name match for methods.
            var targetArity = TryExtractParamCount(member.Value.Signature);
            foreach (var m in typeSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.Name != targetName) continue;
                if (targetArity is null || m.Parameters.Length == targetArity) return m;
            }
            // Finally name match for non-methods.
            foreach (var m in typeSymbol.GetMembers())
            {
                if (m is IMethodSymbol) continue;
                if (m.Name == targetName) return m;
            }

            var sample = string.Join(", ", typeSymbol.GetMembers().Select(m => m.Name).Distinct().Take(8));
            attempts.Add($"{project.Name}: type '{typeSymbol}' found, but no member matched '{targetName}'. Available: [{sample}]");
        }
        if (attempts.Count > 0)
        {
            LastRenameDiagnostic = string.Join("; ", attempts);
        }
        return null;
    }

    private static int? TryExtractParamCount(string signature)
    {
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        var inner = signature.Substring(open + 1, close - open - 1).Trim();
        if (inner.Length == 0) return 0;
        // Count top-level commas (very simple; assumes no nested generics with commas).
        int depth = 0;
        int commas = 0;
        foreach (var ch in inner)
        {
            if (ch == '<' || ch == '(' || ch == '[') depth++;
            else if (ch == '>' || ch == ')' || ch == ']') depth--;
            else if (ch == ',' && depth == 0) commas++;
        }
        return commas + 1;
    }

    // Returns the zero-based position of a parameter within a Roslyn-formatted
    // signature like "Foo(int a, string b, params object[] rest)". -1 if the
    // signature can't be parsed or the name isn't found.
    private static int TryExtractParameterIndex(string signature, string paramName)
    {
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open < 0 || close <= open) return -1;
        var inner = signature.Substring(open + 1, close - open - 1);
        if (string.IsNullOrWhiteSpace(inner)) return -1;

        int depth = 0;
        int start = 0;
        int index = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            var atEnd = i == inner.Length;
            char c = atEnd ? ',' : inner[i];
            if (!atEnd)
            {
                if (c == '<' || c == '(' || c == '[') { depth++; continue; }
                if (c == '>' || c == ')' || c == ']') { depth--; continue; }
            }
            if (c == ',' && depth == 0)
            {
                var part = inner.Substring(start, i - start).TrimEnd();
                int eq = part.IndexOf('=');
                if (eq >= 0) part = part.Substring(0, eq).TrimEnd();
                int spIdx = -1;
                for (int j = part.Length - 1; j >= 0; j--)
                {
                    if (char.IsWhiteSpace(part[j])) { spIdx = j; break; }
                }
                if (spIdx >= 0)
                {
                    var name = part.Substring(spIdx + 1).Trim();
                    if (name == paramName) return index;
                }
                start = i + 1;
                index++;
            }
        }
        return -1;
    }

    private static INamedTypeSymbol? FindTypeInGlobalNamespace(INamespaceSymbol ns, string fullyQualifiedName)
    {
        foreach (var t in ns.GetTypeMembers())
        {
            if (RoslynToModelMapper.ToTypeRef(t).FullyQualifiedName == fullyQualifiedName) return t;
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            var hit = FindTypeInGlobalNamespace(child, fullyQualifiedName);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static string ExtractSimpleName(string signature)
    {
        // Signatures look like "void Connect(...)" or "ConnectionHandle^ handle". Extract the identifier.
        int paren = signature.IndexOf('(');
        var head = paren > 0 ? signature.Substring(0, paren) : signature;
        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : signature;
    }

    private static ReferenceLocation ToReferenceLocation(SyntaxTree tree, TextSpan span, ReferenceKind kind, ReferenceLanguage lang)
    {
        var text = tree.GetText();
        var linePos = text.Lines.GetLinePosition(span.Start);
        var line = text.Lines.GetLineFromPosition(span.Start);
        var snippet = line.ToString().Trim();
        return new ReferenceLocation(
            FilePath: tree.FilePath ?? string.Empty,
            Line: linePos.Line + 1,
            Column: linePos.Character + 1,
            SpanStart: span.Start,
            SpanLength: span.Length,
            LineSnippet: snippet,
            Kind: kind,
            Language: lang);
    }

    private static ReferenceLocation ToReferenceLocation(CppReference r)
    {
        var kind = r.Kind switch
        {
            CppReferenceKind.Declaration => ReferenceKind.Declaration,
            CppReferenceKind.TypeUse => ReferenceKind.TypeUse,
            CppReferenceKind.MethodCall => ReferenceKind.MethodCall,
            CppReferenceKind.MemberAccess => ReferenceKind.MemberAccess,
            _ => ReferenceKind.MemberAccess,
        };
        return new ReferenceLocation(
            FilePath: r.FilePath,
            Line: r.Line,
            Column: r.Column,
            SpanStart: r.SpanStart,
            SpanLength: r.SpanLength,
            LineSnippet: r.LineSnippet,
            Kind: kind,
            Language: ReferenceLanguage.CppCli);
    }

    private static IReadOnlyList<ReferenceLocation> DedupSort(List<ReferenceLocation> input)
    {
        return input
            .DistinctBy(r => (r.FilePath, r.SpanStart, r.SpanLength))
            .OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToList();
    }

    private IReadOnlyList<DocumentChange> BuildCppRenameChanges(CppTypeSymbol cppType, RenameIntent rename)
    {
        if (_cppCompilation is null) return Array.Empty<DocumentChange>();
        if (rename.TargetMember is { } tm)
        {
            var memberName = ExtractMemberNameFromSignature(tm.Signature);
            var info = _cppCompilation.ResolveMember(cppType, memberName);
            var cppMember = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (cppMember is null) return Array.Empty<DocumentChange>();
            return CppRenameEngine.RenameMember(_cppCompilation, cppMember, rename.NewName);
        }
        return CppRenameEngine.RenameType(_cppCompilation, cppType, rename.NewName);
    }

    public string? LastRenameDiagnostic { get; private set; }

    private async Task<IReadOnlyList<DocumentChange>> BuildCSharpRenameChangesAsync(RenameIntent rename, CancellationToken cancellationToken)
    {
        LastRenameDiagnostic = null;
        if (_solution is null)
        {
            LastRenameDiagnostic = "C# side: no solution loaded";
            return Array.Empty<DocumentChange>();
        }

        var target = await ResolveTargetAcrossReferences(rename.TargetType, rename.TargetMember, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            // ResolveTargetAcrossReferences may have already set a detailed diagnostic; keep it if so.
            if (string.IsNullOrEmpty(LastRenameDiagnostic))
            {
                LastRenameDiagnostic = $"C# side: target symbol not resolved for {rename.TargetType.FullyQualifiedName}"
                                     + (rename.TargetMember is null ? " (type)" : $".{rename.TargetMember.Value.Signature}");
            }
            else
            {
                LastRenameDiagnostic = "C# side: " + LastRenameDiagnostic;
            }
            return Array.Empty<DocumentChange>();
        }

        var refs = await SymbolFinder.FindReferencesAsync(target, _solution, cancellationToken).ConfigureAwait(false);
        var byPath = new Dictionary<string, List<TextSpan>>(StringComparer.OrdinalIgnoreCase);
        int totalLocs = 0;
        foreach (var r in refs)
        {
            foreach (var loc in r.Locations)
            {
                totalLocs++;
                if (loc.Location.SourceTree is not { } tree) continue;
                var path = tree.FilePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!byPath.TryGetValue(path, out var list))
                {
                    list = new List<TextSpan>();
                    byPath[path] = list;
                }
                list.Add(loc.Location.SourceSpan);
            }
        }

        if (byPath.Count == 0)
        {
            LastRenameDiagnostic = $"C# side: SymbolFinder returned {totalLocs} location(s) for '{target}' but none had a source path";
            return Array.Empty<DocumentChange>();
        }

        var changes = new List<DocumentChange>(byPath.Count);
        foreach (var (path, spans) in byPath)
        {
            string original;
            try { original = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch { continue; }

            var updated = ApplySpansDescending(original, spans, rename.NewName);
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(path, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }
        LastRenameDiagnostic = $"C# side: resolved '{target}', {totalLocs} loc(s), {changes.Count} file(s) changed";
        return changes;
    }

    private async Task<IReadOnlyList<DocumentChange>> BuildCSharpParameterRenameChangesAsync(
        RenameParameterIntent rnP, CancellationToken cancellationToken)
    {
        if (_solution is null) return Array.Empty<DocumentChange>();

        // Resolve the containing method symbol, then locate the target parameter.
        var methodSymbol = await ResolveTargetAcrossReferences(rnP.OwnerType, rnP.Method, cancellationToken).ConfigureAwait(false) as IMethodSymbol;
        if (methodSymbol is null) return Array.Empty<DocumentChange>();

        IParameterSymbol? paramSymbol = null;
        foreach (var p in methodSymbol.Parameters)
        {
            if (p.Name == rnP.OldName) { paramSymbol = p; break; }
        }
        if (paramSymbol is null) return Array.Empty<DocumentChange>();

        var refs = await SymbolFinder.FindReferencesAsync(paramSymbol, _solution, cancellationToken).ConfigureAwait(false);
        var byPath = new Dictionary<string, List<TextSpan>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in refs)
        {
            foreach (var loc in r.Locations)
            {
                if (loc.Location.SourceTree is not { } tree) continue;
                var path = tree.FilePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!byPath.TryGetValue(path, out var list))
                {
                    list = new List<TextSpan>();
                    byPath[path] = list;
                }
                list.Add(loc.Location.SourceSpan);
            }
        }

        if (byPath.Count == 0) return Array.Empty<DocumentChange>();

        var changes = new List<DocumentChange>(byPath.Count);
        foreach (var (path, spans) in byPath)
        {
            string original;
            try { original = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
            catch { continue; }

            var updated = ApplySpansDescending(original, spans, rnP.NewName);
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(path, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }
        return changes;
    }

    // Rewrites every C# call site of the given method by feeding each call's
    // ArgumentListSyntax through `rewriter`. Handles both invocations (a.M(...))
    // and object creations (new T(...)). Works for cross-language too: the
    // method symbol can come from a Cpp metadata assembly (Phase 4 shim), and
    // SymbolFinder.FindReferencesAsync will surface C# call sites of it.
    private async Task<IReadOnlyList<DocumentChange>> BuildCSharpCallSiteRewriteAsync(
        TypeRef ownerType,
        MemberRef method,
        Func<ArgumentListSyntax, ArgumentListSyntax?> rewriter,
        CancellationToken cancellationToken)
    {
        if (_solution is null) return Array.Empty<DocumentChange>();

        var methodSymbol = await ResolveTargetAcrossReferences(ownerType, method, cancellationToken).ConfigureAwait(false) as IMethodSymbol;
        if (methodSymbol is null) return Array.Empty<DocumentChange>();

        var refs = await SymbolFinder.FindReferencesAsync(methodSymbol, _solution, cancellationToken).ConfigureAwait(false);
        var byDoc = new Dictionary<DocumentId, List<TextSpan>>();
        foreach (var r in refs)
        {
            foreach (var loc in r.Locations)
            {
                if (loc.Document is null) continue;
                if (!byDoc.TryGetValue(loc.Document.Id, out var list))
                {
                    list = new List<TextSpan>();
                    byDoc[loc.Document.Id] = list;
                }
                list.Add(loc.Location.SourceSpan);
            }
        }
        if (byDoc.Count == 0) return Array.Empty<DocumentChange>();

        var changes = new List<DocumentChange>(byDoc.Count);
        foreach (var (docId, spans) in byDoc)
        {
            var doc = _solution.GetDocument(docId);
            if (doc?.FilePath is null) continue;

            var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) continue;

            // Collect (oldList, newList) pairs first, then apply in one ReplaceNodes
            // call so we don't invalidate the tree between edits.
            var replacements = new Dictionary<ArgumentListSyntax, ArgumentListSyntax>();
            foreach (var span in spans)
            {
                var node = root.FindNode(span, getInnermostNodeForTie: true);
                var argList =
                    node.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.ArgumentList
                    ?? node.FirstAncestorOrSelf<ObjectCreationExpressionSyntax>()?.ArgumentList
                    ?? node.FirstAncestorOrSelf<ImplicitObjectCreationExpressionSyntax>()?.ArgumentList
                    ?? node.FirstAncestorOrSelf<BaseObjectCreationExpressionSyntax>()?.ArgumentList;
                if (argList is null) continue;
                if (replacements.ContainsKey(argList)) continue;
                var newList = rewriter(argList);
                if (newList is null || ReferenceEquals(newList, argList)) continue;
                replacements[argList] = newList;
            }
            if (replacements.Count == 0) continue;

            var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
            var newDoc = doc.WithSyntaxRoot(newRoot);
            // Formatter normalizes whitespace we introduced (e.g. missing space
            // after the comma separator when appending a new argument).
            var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(doc.FilePath, DocumentChangeKind.Modified, oldText, newText));
            }
        }
        return changes;
    }

    private static ArgumentListSyntax? AppendArgumentRewriter(ArgumentListSyntax list, string valueText)
    {
        var newArg = SyntaxFactory.Argument(SyntaxFactory.ParseExpression(valueText))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return list.AddArguments(newArg).WithAdditionalAnnotations(Formatter.Annotation);
    }

    // Remove the argument at parameterIndex. Handles named arguments too:
    // if a call uses `paramName: value`, prefer removing by name over index.
    private static ArgumentListSyntax? RemoveArgumentRewriter(
        ArgumentListSyntax list, int parameterIndex, string parameterName)
    {
        var args = list.Arguments;
        ArgumentSyntax? toRemove = null;
        foreach (var a in args)
        {
            if (a.NameColon?.Name.Identifier.ValueText == parameterName)
            {
                toRemove = a;
                break;
            }
        }
        if (toRemove is null && parameterIndex >= 0 && parameterIndex < args.Count)
        {
            toRemove = args[parameterIndex];
        }
        if (toRemove is null) return null;
        return list.WithArguments(args.Remove(toRemove))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static string ApplySpansDescending(string original, IEnumerable<TextSpan> spans, string replacement)
    {
        var ordered = spans
            .Where(s => s.Start >= 0 && s.End <= original.Length)
            .OrderByDescending(s => s.Start)
            .ToList();
        if (ordered.Count == 0) return original;

        var sb = new System.Text.StringBuilder(original);
        foreach (var s in ordered)
        {
            sb.Remove(s.Start, s.Length);
            sb.Insert(s.Start, replacement);
        }
        return sb.ToString();
    }

    private bool IsFromInjectedCppAssembly(INamedTypeSymbol type)
    {
        if (_injectedCppAssemblies.Count == 0) return false;
        var asm = type.ContainingAssembly?.Name;
        return !string.IsNullOrEmpty(asm) && _injectedCppAssemblies.Contains(asm);
    }

    private (TypeRef OwnerType, MemberRef Member)? TryReverseLookupCppSymbol(INamedTypeSymbol containingType, ISymbol member)
    {
        if (_cppCompilation is null) return null;

        var fqn = RoslynToModelMapper.ToTypeRef(containingType).FullyQualifiedName;
        var cppType = _cppCompilation.GetTypeByFullyQualifiedName(fqn);
        if (cppType is null) return null;

        var ownerRef = new TypeRef(cppType.FullyQualifiedName);
        int? arity = member is IMethodSymbol m ? m.Parameters.Length : null;
        var info = _cppCompilation.ResolveMember(cppType, member.Name, arity);
        var cppMember = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (cppMember is null)
        {
            // The type is known, but the member wasn't found in our index.
            // Land on the type declaration so Ctrl+Click still navigates somewhere useful.
            return (ownerRef, new MemberRef(ownerRef, CppTypeSiteSignature));
        }
        return (ownerRef, new MemberRef(ownerRef, cppMember.Signature));
    }

    /// <summary>
    /// Sentinel signature used inside a MemberRef when the click landed on a type name
    /// (or target-typed <c>new()</c>) rather than a member. Signals GetMemberSourceAsync
    /// to navigate to the type's declaration site instead of any member's site.
    /// Shared between the Cpp fallback path and the C# named-type path.
    /// </summary>
    internal const string CppTypeSiteSignature = "<type>";

    private async Task<MemberSource?> BuildCsharpTypeSiteMemberSourceAsync(
        INamedTypeSymbol typeSymbol,
        TypeRef ownerType,
        MemberRef member,
        CancellationToken cancellationToken)
    {
        var sref = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (sref is null)
        {
            return null;
        }

        var doc = _solution!.GetDocument(sref.SyntaxTree);
        if (doc is null || doc.FilePath is null)
        {
            return null;
        }

        var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
        var line = text.Lines.GetLineFromPosition(node.SpanStart);

        return new MemberSource(
            OwnerType: ownerType,
            Member: member,
            FilePath: doc.FilePath,
            SourceText: text.ToString(),
            MemberSpanStart: line.Start,
            MemberSpanLength: line.EndIncludingLineBreak - line.Start,
            BodySpanStart: 0,
            BodySpanLength: 0);
    }

    private MemberSource? TryGetCppFileFunctionSource(TypeRef ownerType, MemberRef member)
    {
        if (_cppCompilation is null) return null;
        var fqn = ownerType.FullyQualifiedName;
        // "<file-fn:{filepath}>" — extract filepath.
        var prefixLen = CppContextClickResolver.FileFunctionOwnerPrefix.Length;
        if (fqn.Length <= prefixLen + 1) return null;
        var filePath = fqn[prefixLen..^1];

        var tree = _cppCompilation.ImplementationTrees
            .Concat(_cppCompilation.SyntaxTrees)
            .FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (tree is null) return null;

        var fnSigPrefix = CppContextClickResolver.FileFunctionSignaturePrefix;
        var macSigPrefix = CppContextClickResolver.MacroSignaturePrefix;

        int line;
        if (member.Signature.StartsWith(fnSigPrefix, StringComparison.Ordinal))
        {
            if (!_cppCompilation.FileFunctionsByFilePath.TryGetValue(filePath, out var fns)) return null;
            var fnName = member.Signature[fnSigPrefix.Length..];
            var fn = fns.FirstOrDefault(f => string.Equals(f.Name, fnName, StringComparison.Ordinal));
            if (fn is null) return null;
            line = fn.Site.Span.Line;
        }
        else if (member.Signature.StartsWith(macSigPrefix, StringComparison.Ordinal))
        {
            if (!_cppCompilation.FileMacrosByFilePath.TryGetValue(filePath, out var macros)) return null;
            var macName = member.Signature[macSigPrefix.Length..];
            var mac = macros.FirstOrDefault(m => string.Equals(m.Name, macName, StringComparison.Ordinal));
            if (mac is null) return null;
            line = mac.Site.Span.Line;
        }
        else
        {
            return null;
        }

        var (lineStart, lineLen) = GetLineSpanForLine(tree.SourceText, line);
        return new MemberSource(
            OwnerType: ownerType,
            Member: member,
            FilePath: filePath,
            SourceText: tree.SourceText,
            MemberSpanStart: lineStart,
            MemberSpanLength: lineLen,
            BodySpanStart: 0,
            BodySpanLength: 0);
    }

    private MemberSource? TryGetCppMemberSource(TypeRef ownerType, MemberRef member)
    {
        if (_cppCompilation is null)
        {
            return null;
        }

        var type = _cppCompilation.GetTypeByFullyQualifiedName(ownerType.FullyQualifiedName);
        if (type is null)
        {
            return null;
        }

        // Type-name / new() click: land on the .h type declaration itself.
        if (string.Equals(member.Signature, CppTypeSiteSignature, StringComparison.Ordinal))
        {
            var typeTree = _cppCompilation.SyntaxTrees.FirstOrDefault(
                t => string.Equals(t.FilePath, type.DeclarationSite.FilePath, StringComparison.OrdinalIgnoreCase));
            if (typeTree is null)
            {
                return null;
            }
            var (typeLineStart, typeLineLen) = GetLineSpanForLine(typeTree.SourceText, type.DeclarationSite.Span.Line);
            return new MemberSource(
                OwnerType: ownerType,
                Member: member,
                FilePath: typeTree.FilePath,
                SourceText: typeTree.SourceText,
                MemberSpanStart: typeLineStart,
                MemberSpanLength: typeLineLen,
                BodySpanStart: 0,
                BodySpanLength: 0);
        }

        var m = type.Members.FirstOrDefault(
            x => string.Equals(x.Signature, member.Signature, StringComparison.Ordinal));
        if (m is null)
        {
            return null;
        }

        var preferImpl = m.ImplementationSite is not null;
        var site = preferImpl ? m.ImplementationSite!.Value : m.DeclarationSite;
        var searchIn = preferImpl
            ? _cppCompilation.ImplementationTrees
            : _cppCompilation.SyntaxTrees;

        var tree = searchIn.FirstOrDefault(
            t => string.Equals(t.FilePath, site.FilePath, StringComparison.OrdinalIgnoreCase));
        if (tree is null)
        {
            return null;
        }

        var (lineStart, lineLen) = GetLineSpanForLine(tree.SourceText, site.Span.Line);
        return new MemberSource(
            OwnerType: ownerType,
            Member: member,
            FilePath: tree.FilePath,
            SourceText: tree.SourceText,
            MemberSpanStart: lineStart,
            MemberSpanLength: lineLen,
            BodySpanStart: 0,
            BodySpanLength: 0);
    }

    private static (int Start, int Length) GetLineSpanForLine(string source, int line)
    {
        if (line < 1) line = 1;
        var currentLine = 1;
        var i = 0;
        var n = source.Length;
        while (currentLine < line && i < n)
        {
            if (source[i] == '\n') currentLine++;
            i++;
        }
        var lineStart = i;
        while (i < n && source[i] != '\n') i++;
        return (lineStart, i - lineStart);
    }

    private static INamedTypeSymbol? TryResolveCsharpNamedTypeFromToken(
        SyntaxToken token,
        SemanticModel semantic,
        CancellationToken cancellationToken)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (IsResolutionBoundary(node))
            {
                if (node is SimpleNameSyntax nameAtBoundary
                    && !IsMemberAccessName(nameAtBoundary))
                {
                    var boundarySymbol = ExtractSourceDefinedNamedType(nameAtBoundary, semantic, cancellationToken);
                    if (boundarySymbol is not null)
                    {
                        return boundarySymbol;
                    }
                }
                break;
            }

            var candidate = node switch
            {
                GenericNameSyntax gn when !IsMemberAccessName(gn)
                    => ExtractSourceDefinedNamedType(gn, semantic, cancellationToken),
                IdentifierNameSyntax id when !IsMemberAccessName(id)
                    => ExtractSourceDefinedNamedType(id, semantic, cancellationToken),
                QualifiedNameSyntax qn
                    => ExtractSourceDefinedNamedType(qn, semantic, cancellationToken),
                _ => null,
            };
            if (candidate is not null)
            {
                return candidate;
            }
        }
        return null;
    }

    private static INamedTypeSymbol? ExtractSourceDefinedNamedType(
        SyntaxNode node,
        SemanticModel semantic,
        CancellationToken cancellationToken)
    {
        var info = semantic.GetSymbolInfo(node, cancellationToken);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is INamedTypeSymbol nt
            && nt.DeclaringSyntaxReferences.Length > 0
            && !nt.IsImplicitlyDeclared)
        {
            return nt.OriginalDefinition as INamedTypeSymbol ?? nt;
        }
        return null;
    }

    private HybridResolveResult? TryHybridResolveFromToken(SyntaxToken token, SemanticModel semantic)
    {
        if (_cppCompilation is null || _cppCompilation.AllTypes.Count == 0)
        {
            return null;
        }

        var resolver = new HybridSymbolResolver(_cppCompilation);
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (IsResolutionBoundary(node))
            {
                // Even at the boundary, give the type-name fallback one shot —
                // e.g. field declarations sit right on top of MemberDeclarationSyntax.
                if (node is SimpleNameSyntax nameAtBoundary
                    && !IsMemberAccessName(nameAtBoundary))
                {
                    var typeResult = resolver.TryResolveTypeName(nameAtBoundary);
                    if (typeResult is not null)
                    {
                        return typeResult;
                    }
                }
                break;
            }
            if (node is MemberAccessExpressionSyntax ma)
            {
                var result = resolver.TryResolveMemberAccess(ma, semantic);
                if (result is not null)
                {
                    return result;
                }
            }
            if (node is ImplicitObjectCreationExpressionSyntax implicitNew)
            {
                var result = resolver.TryResolveImplicitObjectCreation(implicitNew);
                if (result is not null)
                {
                    return result;
                }
            }
            if (node is SimpleNameSyntax name && !IsMemberAccessName(name))
            {
                var result = resolver.TryResolveTypeName(name);
                if (result is not null)
                {
                    return result;
                }
            }
        }
        return null;
    }

    private static bool IsMemberAccessName(SimpleNameSyntax name)
        => name.Parent is MemberAccessExpressionSyntax ma && ReferenceEquals(ma.Name, name);

    private static ISymbol? ResolveSymbolNearToken(SyntaxToken token, SemanticModel semantic, CancellationToken cancellationToken)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (IsResolutionBoundary(node)) break;

            var symbol = TryResolve(semantic, node, cancellationToken);

            if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            {
                return symbol;
            }

            if (symbol is INamedTypeSymbol
                && node.Parent is ObjectCreationExpressionSyntax parentOc
                && ReferenceEquals(parentOc.Type, node))
            {
                var ctor = FirstNavigable(semantic.GetSymbolInfo(parentOc, cancellationToken));
                if (ctor is IMethodSymbol) return ctor;
            }
        }
        return null;
    }

    private static bool IsResolutionBoundary(SyntaxNode node) =>
        node is StatementSyntax
            or ArgumentSyntax
            or LambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            or MemberDeclarationSyntax
            or EqualsValueClauseSyntax
            or AttributeSyntax;

    private static ISymbol? TryResolve(SemanticModel semantic, SyntaxNode node, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case InvocationExpressionSyntax inv:
                return FirstNavigable(semantic.GetSymbolInfo(inv, cancellationToken))
                    ?? FirstNavigable(semantic.GetSymbolInfo(inv.Expression, cancellationToken));
            case ObjectCreationExpressionSyntax oc:
                return FirstNavigable(semantic.GetSymbolInfo(oc, cancellationToken));
            case MemberAccessExpressionSyntax ma:
                return FirstNavigable(semantic.GetSymbolInfo(ma, cancellationToken))
                    ?? FirstNavigable(semantic.GetSymbolInfo(ma.Name, cancellationToken));
            case IdentifierNameSyntax id:
                return FirstNavigable(semantic.GetSymbolInfo(id, cancellationToken));
            case GenericNameSyntax gn:
                return FirstNavigable(semantic.GetSymbolInfo(gn, cancellationToken));
            default:
                return null;
        }
    }

    private static string DescribeResolveFailure(SyntaxToken token, SemanticModel semantic, CancellationToken cancellationToken)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (IsResolutionBoundary(node)) break;
            var info = node switch
            {
                InvocationExpressionSyntax inv => semantic.GetSymbolInfo(inv, cancellationToken),
                ObjectCreationExpressionSyntax oc => semantic.GetSymbolInfo(oc, cancellationToken),
                MemberAccessExpressionSyntax ma => semantic.GetSymbolInfo(ma, cancellationToken),
                IdentifierNameSyntax id => semantic.GetSymbolInfo(id, cancellationToken),
                GenericNameSyntax gn => semantic.GetSymbolInfo(gn, cancellationToken),
                _ => (SymbolInfo?)null,
            };
            if (info is null) continue;
            var i = info.Value;
            if (i.Symbol is not null || i.CandidateSymbols.Length > 0)
            {
                return $"reason={i.CandidateReason} candidates={i.CandidateSymbols.Length} at {node.GetType().Name}";
            }
            if (node is MemberAccessExpressionSyntax memberAccess)
            {
                var receiverType = semantic.GetTypeInfo(memberAccess.Expression, cancellationToken).Type;
                if (receiverType is null || receiverType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error)
                {
                    return $"receiver '{memberAccess.Expression}' has unresolved type — check project references / compile errors";
                }
            }
        }
        return "SemanticModel returned nothing at every ancestor (source may have compile errors)";
    }

    private static ISymbol? FirstNavigable(SymbolInfo info)
    {
        if (info.Symbol is not null) return info.Symbol;
        foreach (var candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol)
            {
                return candidate;
            }
        }
        return info.CandidateSymbols.FirstOrDefault();
    }

    private static (int Start, int Length) GetBodySpan(SyntaxNode node)
    {
        switch (node)
        {
            case MethodDeclarationSyntax m when m.Body is not null:
                return (m.Body.Span.Start, m.Body.Span.Length);
            case MethodDeclarationSyntax m when m.ExpressionBody is not null:
                return (m.ExpressionBody.Expression.Span.Start, m.ExpressionBody.Expression.Span.Length);
            case ConstructorDeclarationSyntax c when c.Body is not null:
                return (c.Body.Span.Start, c.Body.Span.Length);
            case AccessorDeclarationSyntax a when a.Body is not null:
                return (a.Body.Span.Start, a.Body.Span.Length);
            case PropertyDeclarationSyntax p when p.ExpressionBody is not null:
                return (p.ExpressionBody.Expression.Span.Start, p.ExpressionBody.Expression.Span.Length);
            default:
                return (node.Span.Start, node.Span.Length);
        }
    }

    /// <summary>
    /// Optional per-phase timing callback for observability. Set by the App layer; each
    /// stage of LoadSolutionAsync invokes it with (label, elapsed ms) when done.
    /// </summary>
    public static Action<string, long>? OnLoadPhaseCompleted;
    public static Action<string>? OnLoadPhaseStarted;
    public static Action<string>? OnLoadPhaseEnded;

    private static long MeasurePhase(string label, Action work)
    {
        OnLoadPhaseStarted?.Invoke(label);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { work(); }
        finally
        {
            sw.Stop();
            OnLoadPhaseCompleted?.Invoke(label, sw.ElapsedMilliseconds);
            OnLoadPhaseEnded?.Invoke(label);
        }
        return sw.ElapsedMilliseconds;
    }

    private static async Task<T> MeasurePhaseAsync<T>(string label, Func<Task<T>> work)
    {
        OnLoadPhaseStarted?.Invoke(label);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await work().ConfigureAwait(false);
            return result;
        }
        finally
        {
            sw.Stop();
            OnLoadPhaseCompleted?.Invoke(label, sw.ElapsedMilliseconds);
            OnLoadPhaseEnded?.Invoke(label);
        }
    }

    public async Task<SolutionModel> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        MsBuildHost.EnsureRegistered();

        _workspace?.Dispose();
        _workspace = MSBuildWorkspace.Create();
        _solution = await MeasurePhaseAsync("open_sln", () =>
            _workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken)).ConfigureAwait(false);

        IReadOnlyList<DiscoveredProject> discovered = Array.Empty<DiscoveredProject>();
        MeasurePhase("discover", () =>
        {
            discovered = SolutionProjectDiscovery.DiscoverForeignProjects(solutionPath, ForeignExtensions);
        });
        _lastDiscoveredForeignProjects = discovered;

        MeasurePhase("cpp_compile", () => { _cppCompilation = BuildCppCompilation(discovered); });
        MeasurePhase("inject_shim", () => { _solution = InjectCppShimReferences(_solution, discovered); });

        var managedModel = await MeasurePhaseAsync("map_async", () => MapAsync(_solution, cancellationToken)).ConfigureAwait(false);

        IReadOnlyList<ProjectModel> foreignProjects = Array.Empty<ProjectModel>();
        MeasurePhase("foreign_projects", () => { foreignProjects = BuildForeignProjectModels(discovered, _cppCompilation); });
        _lastForeignProjects = foreignProjects;

        return foreignProjects.Count == 0
            ? managedModel
            : managedModel with { Projects = managedModel.Projects.Concat(foreignProjects).ToList() };
    }

    private MsSolution InjectCppShimReferences(MsSolution solution, IReadOnlyList<DiscoveredProject> discovered)
    {
        _injectedCppAssemblies.Clear();
        _staleCppShimWarnings.Clear();
        if (discovered.Count == 0) return solution;

        // Resolve a DLL for each vcxproj (fresh preferred, stale accepted with a warning).
        var vcxToDll = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in discovered)
        {
            if (!d.Extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase)) continue;
            var res = CppShimReferenceResolver.ResolveDll(d.AbsolutePath);
            if (!res.HasDll) continue;
            vcxToDll[NormalizePath(d.AbsolutePath)] = res.DllPath!;
            if (!res.IsFresh)
            {
                var age = res.NewestSourceUtc is { } src && src > res.DllUtc
                    ? $" (source {src:HH:mm} > dll {res.DllUtc:HH:mm})"
                    : string.Empty;
                _staleCppShimWarnings.Add($"{Path.GetFileName(d.AbsolutePath)}: injected stale DLL {res.DllPath}{age} — rebuild the C++/CLI project for accurate cross-language rename/find-refs");
            }
        }
        if (vcxToDll.Count == 0) return solution;

        foreach (var project in solution.Projects.ToList())
        {
            if (project.FilePath is null) continue;

            var referencedVcxProjs = ReadCsProjectReferences(project.FilePath, ".vcxproj");
            foreach (var vcxAbs in referencedVcxProjs)
            {
                if (!vcxToDll.TryGetValue(NormalizePath(vcxAbs), out var dllPath)) continue;
                var reference = MetadataReference.CreateFromFile(dllPath);
                solution = solution.AddMetadataReference(project.Id, reference);
                _injectedCppAssemblies.Add(Path.GetFileNameWithoutExtension(dllPath));
            }
        }

        return solution;
    }

    private static IReadOnlyList<string> ReadCsProjectReferences(string csprojPath, string targetExtension)
    {
        if (!File.Exists(csprojPath)) return Array.Empty<string>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            var csprojDir = Path.GetDirectoryName(csprojPath)!;
            var result = new List<string>();
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = el.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                if (!include.EndsWith(targetExtension, StringComparison.OrdinalIgnoreCase)) continue;
                var abs = Path.GetFullPath(Path.Combine(csprojDir, include));
                result.Add(abs);
            }
            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);

    private static readonly HashSet<string> ForeignExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vcxproj",
    };

    private static CppCompilation BuildCppCompilation(IReadOnlyList<DiscoveredProject> discovered)
    {
        // Fan-out per vcxproj — each project's header/impl parse is independent.
        // Header set and impl set for the same project are ALSO independent, so
        // we fan out to 2 * N tasks and gather at the end.
        var vcxprojs = discovered
            .Where(d => d.Extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var headerTasks = vcxprojs.Select(d => Task.Run(() => CppCliProjectLoader.LoadSyntaxTrees(d.AbsolutePath))).ToArray();
        var implTasks = vcxprojs.Select(d => Task.Run(() => CppCliProjectLoader.LoadImplementationTrees(d.AbsolutePath))).ToArray();

        Task.WaitAll(headerTasks.Concat(implTasks).ToArray<Task>());

        var headerTrees = headerTasks.SelectMany(t => t.Result).ToList();
        var implTrees = implTasks.SelectMany(t => t.Result).ToList();
        return CppCompilation.Create(headerTrees, implTrees);
    }

    private static IReadOnlyList<ProjectModel> BuildForeignProjectModels(
        IReadOnlyList<DiscoveredProject> discovered,
        CppCompilation? cppCompilation)
    {
        if (discovered.Count == 0)
        {
            return Array.Empty<ProjectModel>();
        }

        // Parse every foreign project in parallel — each ParseProject call is pure and
        // independent (reads its own .vcxproj + headers). Sequential parsing here was
        // the second-biggest sln-load bottleneck after CppCliProjectLoader.
        var parseTasks = discovered
            .Select(d => Task.Run(() =>
            {
                var languageId = MapExtensionToLanguage(d.Extension);
                IReadOnlyList<TypeModel> types = d.Extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase)
                    ? CppCliProjectParser.ParseProject(d.AbsolutePath)
                    : Array.Empty<TypeModel>();
                return (Discovered: d, LanguageId: languageId, Types: types);
            }))
            .ToArray();
        Task.WaitAll(parseTasks);

        var projects = new List<ProjectModel>(discovered.Count);
        foreach (var t in parseTasks)
        {
            var (d, languageId, types) = t.Result;
            if (types.Count == 0)
            {
                // Phase 1 fallback: at least show a project-level placeholder.
                var ns = new NamespaceRef(d.Name);
                var typeRef = new TypeRef($"{d.Name}.{d.Name}");
                var placeholder = new TypeModel(
                    Ref: typeRef,
                    Name: d.Name,
                    Namespace: ns,
                    Kind: Kata.Core.Model.TypeKind.Unknown,
                    Accessibility: MemberAccessibility.Public,
                    Members: Array.Empty<MemberModel>(),
                    BaseTypes: Array.Empty<TypeRef>(),
                    ImplementedInterfaces: Array.Empty<TypeRef>(),
                    IsGhost: false,
                    IsForeignProject: true);
                types = new[] { placeholder };
            }

            // Cpp/CLI の .cpp 内 file-static / free function を擬似型としてクラス図に出す。
            // 実クラスと FQN が衝突しないよう名前空間はプロジェクト名、Name はファイル名。
            // Extract Method で file-static ヘルパーを切り出しても diagram に反映されるように。
            if (d.Extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase)
                && cppCompilation is not null)
            {
                var pseudoTypes = BuildFileScopePseudoTypes(d, cppCompilation);
                if (pseudoTypes.Count > 0)
                {
                    types = types.Concat(pseudoTypes).ToList();
                }

                // 各実型のメンバー本体を走査して、参照している他の Cpp 型名を
                // BodyReferencedTypeNames に載せる。SolutionGraphBuilder が uses エッジを
                // 引く元ネタとして使う。これで Extract Method 後にヘルパーへの矢印が出る。
                types = AugmentWithBodyReferences(types, cppCompilation);
            }

            projects.Add(new ProjectModel(
                Name: d.Name,
                FilePath: d.AbsolutePath,
                LanguageId: languageId,
                Types: types));
        }
        return projects;
    }

    // 各 TypeModel の member 本体を CppCompilation 経由で読み、そこに現れる識別子で
    // 「その compilation に存在する型の短名」に一致するものを BodyReferencedTypeNames に
    // 蓄積して返す。自分自身は除外。SolutionGraphBuilder.ExtractReferencedTypeNames の
    // 補完ソースとして機能させる。
    private static IReadOnlyList<TypeModel> AugmentWithBodyReferences(
        IReadOnlyList<TypeModel> types,
        CppCompilation cppCompilation)
    {
        // 短名 → 存在する全型 FQN の逆引き。1 パスで build。
        var shortNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in cppCompilation.AllTypes) shortNames.Add(t.Name);

        // pseudo type の Member 名 (macro or file-scope function) → pseudo type Name (fileName)
        // マッピング。body 内に "DEBUG_LOG" や "ConnectPipelineToSource" が現れたら、それを
        // 含む pseudo type ("pch.h" / "SourceConnectHelper.h") への uses edge を張るため。
        // SolutionGraphBuilder は byShortName に pseudo type の fileName も extra key として
        // 登録するので、この Name で lookup が通る。
        var memberToPseudoTypeName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            if (!LooksLikeFileScopePseudoType(t.Name)) continue;
            foreach (var m in t.Members)
            {
                // 同名 member が複数 pseudo type にある場合最初の 1 個に紐付く。
                // (実 C++/CLI codebase では M_PI が複数 .cpp に define されているケースがあるが、細分化する
                // 意味は薄い — 最初のヒットで uses edge を張れれば Impact Focus 上等)
                memberToPseudoTypeName.TryAdd(m.Name, t.Name);
            }
        }

        if (shortNames.Count == 0 && memberToPseudoTypeName.Count == 0) return types;

        var result = new List<TypeModel>(types.Count);
        foreach (var t in types)
        {
            var cppType = cppCompilation.GetTypeByFullyQualifiedName(t.Ref.FullyQualifiedName);
            if (cppType is null) { result.Add(t); continue; }

            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in cppType.Members)
            {
                var body = cppCompilation.TryGetMemberBody(m);
                if (string.IsNullOrEmpty(body)) continue;
                ScanIdentifiers(body, shortNames, memberToPseudoTypeName, refs);
            }
            refs.Remove(t.Name); // 自分自身は uses と見なさない

            if (refs.Count == 0) { result.Add(t); continue; }
            result.Add(t with { BodyReferencedTypeNames = refs.ToList() });
        }
        return result;
    }

    // 素朴な識別子スキャナー: [A-Za-z_][A-Za-z0-9_]* を全部取り出して、shortNames と交差した
    // ものを sink に足す。文字列リテラルやコメント内も見てしまうが、smell 用ではなく uses
    // 補助なので過剰検出は許容 (むしろ拾い漏れが痛い)。
    // pseudoMemberMap にヒットしたら pseudo type Name (fileName) に昇格して sink に入れる。
    private static void ScanIdentifiers(
        string text,
        HashSet<string> knownNames,
        Dictionary<string, string> pseudoMemberMap,
        HashSet<string> sink)
    {
        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text.Substring(start, i - start);
                if (knownNames.Contains(word)) sink.Add(word);
                else if (pseudoMemberMap.TryGetValue(word, out var pseudoName)) sink.Add(pseudoName);
            }
            else
            {
                i++;
            }
        }
    }

    // Kata.App.Graph.SolutionGraphBuilder.LooksLikeFileScopePseudoType のミラー。
    // pseudo type 判定は Roslyn 側でも必要 (AugmentWithBodyReferences の member 収集で
    // pseudo type だけを対象にするため)。両方の判定基準を揃えるため片方だけ変更しないこと。
    private static bool LooksLikeFileScopePseudoType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        var ext = Path.GetExtension(typeName);
        return ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hh", StringComparison.OrdinalIgnoreCase);
    }

    // Cpp/CLI の .cpp ファイル内に定義された file-static / free function を、
    // 「ファイル 1 個 = 擬似 TypeModel 1 個」の形でモデル化する。
    // 関連付けは実 .cpp のディレクトリ (case-insensitive prefix) が vcxproj のディレクトリ配下かで判定。
    private static IReadOnlyList<TypeModel> BuildFileScopePseudoTypes(
        DiscoveredProject vcxproj,
        CppCompilation cppCompilation)
    {
        var vcxprojDir = Path.GetDirectoryName(vcxproj.AbsolutePath);
        if (string.IsNullOrEmpty(vcxprojDir)) return Array.Empty<TypeModel>();
        var vcxprojDirWithSep = vcxprojDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

        var pseudoNamespace = new NamespaceRef(vcxproj.Name);
        var result = new List<TypeModel>();

        // Union of files that contribute file-scope functions OR macros — both
        // groups produce one pseudo TypeModel per file, so we iterate the union
        // to avoid duplicate TypeModels for the same file.
        var files = new HashSet<string>(cppCompilation.FileFunctionsByFilePath.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in cppCompilation.FileMacrosByFilePath.Keys) files.Add(k);

        foreach (var filePath in files)
        {
            var functions = cppCompilation.FileFunctionsByFilePath.TryGetValue(filePath, out var fs)
                ? fs : (IReadOnlyList<CppFileFunctionSymbol>)Array.Empty<CppFileFunctionSymbol>();
            var macros = cppCompilation.FileMacrosByFilePath.TryGetValue(filePath, out var ms)
                ? ms : (IReadOnlyList<CppMacroSymbol>)Array.Empty<CppMacroSymbol>();
            if (functions.Count == 0 && macros.Count == 0) continue;

            // Only claim this file if it lives under this vcxproj's directory tree.
            if (!filePath.StartsWith(vcxprojDirWithSep, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = Path.GetFileName(filePath);
            var typeRef = new TypeRef($"{vcxproj.Name}.{fileName}");

            var members = new List<MemberModel>(functions.Count + macros.Count);
            foreach (var ff in functions)
            {
                var parameters = ParseFileFunctionParameters(ff.ParameterListText);
                var paramKeys = parameters.Select(p =>
                    new SymbolKeyFormatter.ParameterKey(p.TypeDisplay, p.Name)).ToArray();
                var signature = SymbolKeyFormatter.FormatMethodSignature(
                    returnTypeDisplay: ff.ReturnTypeText,
                    name: ff.Name,
                    parameters: paramKeys);
                members.Add(new MemberModel(
                    Ref: new MemberRef(typeRef, signature),
                    Name: ff.Name,
                    Kind: MemberKind.Method,
                    Accessibility: MemberAccessibility.Internal,
                    ReturnTypeDisplay: ff.ReturnTypeText,
                    IsStatic: true,
                    Parameters: parameters));
            }
            foreach (var mac in macros)
            {
                if (mac.IsFunctionLike)
                {
                    // Function-like macro: Method-shaped member. Preprocessor has no type
                    // system, so parameter types are blank and the return type is empty.
                    var macParams = mac.Parameters
                        .Select(p => new ParameterModel(p, string.Empty))
                        .ToArray();
                    var paramKeys = macParams
                        .Select(p => new SymbolKeyFormatter.ParameterKey(p.TypeDisplay, p.Name))
                        .ToArray();
                    var signature = SymbolKeyFormatter.FormatMethodSignature(
                        returnTypeDisplay: string.Empty,
                        name: mac.Name,
                        parameters: paramKeys);
                    members.Add(new MemberModel(
                        Ref: new MemberRef(typeRef, signature),
                        Name: mac.Name,
                        Kind: MemberKind.Method,
                        Accessibility: MemberAccessibility.Internal,
                        ReturnTypeDisplay: string.Empty,
                        IsStatic: true,
                        Parameters: macParams,
                        IsReadOnly: false,
                        IsGhost: false,
                        IsMacro: true));
                }
                else
                {
                    // Object-like macro: Field-shaped member. ReturnTypeDisplay carries the
                    // replacement text so the diagram row reads `« macro » NAME : 128`.
                    var signature = SymbolKeyFormatter.FormatFieldSignature(mac.Name);
                    members.Add(new MemberModel(
                        Ref: new MemberRef(typeRef, signature),
                        Name: mac.Name,
                        Kind: MemberKind.Field,
                        Accessibility: MemberAccessibility.Internal,
                        ReturnTypeDisplay: mac.ReplacementText,
                        IsStatic: true,
                        Parameters: Array.Empty<ParameterModel>(),
                        IsReadOnly: false,
                        IsGhost: false,
                        IsMacro: true));
                }
            }

            result.Add(new TypeModel(
                Ref: typeRef,
                Name: fileName,
                Namespace: pseudoNamespace,
                Kind: Kata.Core.Model.TypeKind.Class,
                Accessibility: MemberAccessibility.Internal,
                Members: members,
                BaseTypes: Array.Empty<TypeRef>(),
                ImplementedInterfaces: Array.Empty<TypeRef>(),
                IsAbstract: false,
                IsStatic: true, // file-scope 関数群は全て "static" 相当
                IsGhost: false,
                IsForeignProject: false));
        }

        return result;
    }

    // "int a, AudioBuffer^ b, const std::vector<int>& v" のような生テキストを
    // 頂点 (トップレベル) のカンマで分割し、各要素を「最後の識別子 = 名前」「その前 = 型」に割る。
    // 保守的な heuristic — 失敗した要素は "arg{i}" と生テキストで穴埋め。
    private static IReadOnlyList<ParameterModel> ParseFileFunctionParameters(string paramListText)
    {
        if (string.IsNullOrWhiteSpace(paramListText)) return Array.Empty<ParameterModel>();

        var parts = SplitTopLevelCommas(paramListText);
        var list = new List<ParameterModel>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            var raw = parts[i].Trim();
            if (raw.Length == 0) continue;
            var (typeText, name) = SplitParameterTypeAndName(raw, i);
            list.Add(new ParameterModel(name, typeText));
        }
        return list;
    }

    private static List<string> SplitTopLevelCommas(string text)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '<' or '[' or '{') depth++;
            else if (c is ')' or '>' or ']' or '}') { if (depth > 0) depth--; }
            else if (c == ',' && depth == 0)
            {
                result.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start < text.Length) result.Add(text.Substring(start));
        return result;
    }

    // "AudioBuffer^ buffer" → ("AudioBuffer^", "buffer")
    // "int" → ("int", "arg0") — 型のみ、名前無しの宣言に fallback
    private static (string TypeText, string Name) SplitParameterTypeAndName(string raw, int index)
    {
        // 末尾の識別子を取り出す
        int end = raw.Length;
        int i = end - 1;
        while (i >= 0 && (char.IsLetterOrDigit(raw[i]) || raw[i] == '_')) i--;
        if (i < 0 || i == end - 1)
        {
            // 名前らしきものが無い (型のみ)
            return (raw.Trim(), $"arg{index}");
        }
        var name = raw.Substring(i + 1).Trim();
        var typeText = raw.Substring(0, i + 1).Trim();
        return (typeText, name);
    }

    private static string MapExtensionToLanguage(string extension) => extension.ToLowerInvariant() switch
    {
        ".vcxproj" => "cpp-cli",
        _ => "unknown",
    };

    private static string ExtractShortName(string fullyQualifiedName)
    {
        var lastDot = fullyQualifiedName.LastIndexOf('.');
        return lastDot < 0 ? fullyQualifiedName : fullyQualifiedName[(lastDot + 1)..];
    }

    private static string StripSignatureParens(string signature)
    {
        var paren = signature.IndexOf('(');
        return paren < 0 ? signature : signature[..paren];
    }

    private static string ExtractMemberNameFromSignature(string signature)
    {
        var paren = signature.IndexOf('(');
        var beforeParen = paren < 0 ? signature : signature[..paren];
        var lastSpace = beforeParen.LastIndexOf(' ');
        return lastSpace < 0 ? beforeParen : beforeParen[(lastSpace + 1)..];
    }

    public async Task<ChangeSet> ProposeChangesAsync(
        SolutionModel model,
        IReadOnlyList<RefactoringIntent> intents,
        CancellationToken cancellationToken = default)
    {
        if (_solution is null)
        {
            throw new InvalidOperationException("Load a solution before proposing changes.");
        }

        var solution = _solution;
        var appliedIds = new List<Guid>();
        var docChanges = new List<DocumentChange>();

        foreach (var intent in intents)
        {
            switch (intent)
            {
                case RenameIntent rename:
                {
                    // Cpp-defined target: use semantic CppRenameEngine + cross-language
                    // C# usage rewrite via SymbolFinder (metadata symbols supported thanks to
                    // Phase 4's DLL injection).
                    if (_cppCompilation is not null
                        && _cppCompilation.GetTypeByFullyQualifiedName(rename.TargetType.FullyQualifiedName) is { } cppType)
                    {
                        // Fan-out: Cpp text rewrite and C# SymbolFinder-driven rewrite are
                        // independent inputs to the merged ChangeSet — no data flows between
                        // them. Diamond: run both in parallel then merge.
                        var cppChangesTask = Task.Run(() => BuildCppRenameChanges(cppType, rename), cancellationToken);
                        var csChangesTask = BuildCSharpRenameChangesAsync(rename, cancellationToken);
                        await Task.WhenAll(cppChangesTask, csChangesTask).ConfigureAwait(false);
                        var cppRenameChanges = cppChangesTask.Result;
                        if (cppRenameChanges.Count > 0)
                        {
                            docChanges.AddRange(cppRenameChanges);
                            docChanges.AddRange(csChangesTask.Result);
                            appliedIds.Add(intent.Id);
                            break;
                        }
                        // Fall through to legacy path only if the semantic engine produced nothing.
                    }

                    if (CppCliRefactorEngine.TryFindTargetByType(model, rename.TargetType, out var cppTarget)
                        && cppTarget is not null)
                    {
                        var oldName = rename.TargetMember is { } m
                            ? ExtractMemberNameFromSignature(m.Signature)
                            : ExtractShortName(rename.TargetType.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.Rename(cppTarget, oldName, rename.NewName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var (newSolution, csChanges) = await ApplyRenameAsync(solution, rename, cancellationToken).ConfigureAwait(false);
                    solution = newSolution;
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ExtractInterfaceIntent extract:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, extract.SourceType, out var cppTarget)
                        && cppTarget is not null)
                    {
                        var sourceTypeName = ExtractShortName(extract.SourceType.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.ExtractInterface(cppTarget, extract, sourceTypeName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var (newSolution, csChanges) = await ApplyExtractInterfaceAsync(solution, extract, cancellationToken).ConfigureAwait(false);
                    solution = newSolution;
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ExtractSuperclassIntent extractSuper:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, extractSuper.SourceType, out var cppTargetForSuper)
                        && cppTargetForSuper is not null)
                    {
                        var sourceTypeName = ExtractShortName(extractSuper.SourceType.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.ExtractSuperclass(cppTargetForSuper, extractSuper, sourceTypeName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var (newSolution, csChanges) = await ApplyExtractSuperclassAsync(solution, extractSuper, cancellationToken).ConfigureAwait(false);
                    solution = newSolution;
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ExtractClassIntent extractCls:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, extractCls.SourceType, out var cppTargetForCls)
                        && cppTargetForCls is not null)
                    {
                        var sourceTypeName = ExtractShortName(extractCls.SourceType.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.ExtractClass(cppTargetForCls, extractCls, sourceTypeName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var (newSolution, csChanges) = await ApplyExtractClassAsync(solution, extractCls, cancellationToken).ConfigureAwait(false);
                    solution = newSolution;
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case RemoveSubclassIntent remove:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, remove.Subclass, out var cppTargetForRemove)
                        && cppTargetForRemove is not null)
                    {
                        var subName = ExtractShortName(remove.Subclass.FullyQualifiedName);
                        var baseName = ExtractShortName(remove.ReplacementBase.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.RemoveSubclass(cppTargetForRemove, subName, baseName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = ApplyRemoveSubclass(solution, remove);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case CollapseHierarchyIntent collapse:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, collapse.Subclass, out var cppTargetForCollapse)
                        && cppTargetForCollapse is not null)
                    {
                        var subName = ExtractShortName(collapse.Subclass.FullyQualifiedName);
                        var parentName = ExtractShortName(collapse.Parent.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.CollapseHierarchy(cppTargetForCollapse, subName, parentName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyCollapseHierarchyAsync(solution, collapse, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case PullUpMethodIntent pullUpMethod:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, pullUpMethod.Subclass, out var cppTargetForPum)
                        && cppTargetForPum is not null)
                    {
                        var subName = ExtractShortName(pullUpMethod.Subclass.FullyQualifiedName);
                        var parentName = ExtractShortName(pullUpMethod.Parent.FullyQualifiedName);
                        var memberNames = pullUpMethod.Members
                            .Select(m => ExtractMemberNameFromSignature(m.Signature))
                            .ToArray();
                        var cppChanges = CppCliRefactorEngine.MoveMembersBetweenClasses(cppTargetForPum, subName, parentName, memberNames);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await MoveMembersBetweenClassesAsync(solution, pullUpMethod.Subclass, pullUpMethod.Parent, pullUpMethod.Members, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case PushDownMethodIntent pushDownMethod:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, pushDownMethod.Parent, out var cppTargetForPdm)
                        && cppTargetForPdm is not null)
                    {
                        var parentName = ExtractShortName(pushDownMethod.Parent.FullyQualifiedName);
                        var subName = ExtractShortName(pushDownMethod.Subclass.FullyQualifiedName);
                        var memberNames = pushDownMethod.Members
                            .Select(m => ExtractMemberNameFromSignature(m.Signature))
                            .ToArray();
                        var cppChanges = CppCliRefactorEngine.MoveMembersBetweenClasses(cppTargetForPdm, parentName, subName, memberNames);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await MoveMembersBetweenClassesAsync(solution, pushDownMethod.Parent, pushDownMethod.Subclass, pushDownMethod.Members, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case PullUpFieldIntent pullUpField:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, pullUpField.Subclass, out var cppTargetForPuf)
                        && cppTargetForPuf is not null)
                    {
                        var subName = ExtractShortName(pullUpField.Subclass.FullyQualifiedName);
                        var parentName = ExtractShortName(pullUpField.Parent.FullyQualifiedName);
                        var memberNames = pullUpField.Members
                            .Select(m => ExtractMemberNameFromSignature(m.Signature))
                            .ToArray();
                        var cppChanges = CppCliRefactorEngine.MoveMembersBetweenClasses(cppTargetForPuf, subName, parentName, memberNames);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await MoveMembersBetweenClassesAsync(solution, pullUpField.Subclass, pullUpField.Parent, pullUpField.Members, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case PushDownFieldIntent pushDownField:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, pushDownField.Parent, out var cppTargetForPdf)
                        && cppTargetForPdf is not null)
                    {
                        var parentName = ExtractShortName(pushDownField.Parent.FullyQualifiedName);
                        var subName = ExtractShortName(pushDownField.Subclass.FullyQualifiedName);
                        var memberNames = pushDownField.Members
                            .Select(m => ExtractMemberNameFromSignature(m.Signature))
                            .ToArray();
                        var cppChanges = CppCliRefactorEngine.MoveMembersBetweenClasses(cppTargetForPdf, parentName, subName, memberNames);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await MoveMembersBetweenClassesAsync(solution, pushDownField.Parent, pushDownField.Subclass, pushDownField.Members, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case RemoveSettingMethodIntent removeSet:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, removeSet.OwnerType, out var cppTargetForRsm)
                        && cppTargetForRsm is not null)
                    {
                        var ownerName = ExtractShortName(removeSet.OwnerType.FullyQualifiedName);
                        var propName = ExtractMemberNameFromSignature(removeSet.Property.Signature);
                        var cppChanges = CppCliRefactorEngine.RemoveSettingMethod(cppTargetForRsm, ownerName, propName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyRemoveSettingMethodAsync(solution, removeSet, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case RenameFieldIntent renameField:
                {
                    // Cpp-defined target: semantic rewrite + cross-language C# rewrite.
                    if (_cppCompilation is not null
                        && _cppCompilation.GetTypeByFullyQualifiedName(renameField.OwnerType.FullyQualifiedName) is { } cppRfType)
                    {
                        var syntheticRenameIntent = IntentFactory.Rename(
                            targetType: renameField.OwnerType,
                            newName: renameField.NewName,
                            source: renameField.Source,
                            rationale: renameField.Rationale,
                            targetMember: renameField.Field);
                        var cppChangesTaskRf = Task.Run(() => BuildCppRenameChanges(cppRfType, syntheticRenameIntent), cancellationToken);
                        var csChangesTaskRf = BuildCSharpRenameChangesAsync(syntheticRenameIntent, cancellationToken);
                        await Task.WhenAll(cppChangesTaskRf, csChangesTaskRf).ConfigureAwait(false);
                        var cppRenameChangesRf = cppChangesTaskRf.Result;
                        if (cppRenameChangesRf.Count > 0)
                        {
                            docChanges.AddRange(cppRenameChangesRf);
                            docChanges.AddRange(csChangesTaskRf.Result);
                            appliedIds.Add(intent.Id);
                            break;
                        }
                        // Fall through to legacy path only if the semantic engine produced nothing.
                    }

                    if (CppCliRefactorEngine.TryFindTargetByType(model, renameField.OwnerType, out var cppTargetForRf)
                        && cppTargetForRf is not null)
                    {
                        var oldName = ExtractMemberNameFromSignature(renameField.Field.Signature);
                        var cppChanges = CppCliRefactorEngine.Rename(cppTargetForRf, oldName, renameField.NewName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    // Delegate to the same Roslyn Renamer path used for regular Rename.
                    var renameIntent = IntentFactory.Rename(
                        targetType: renameField.OwnerType,
                        newName: renameField.NewName,
                        source: renameField.Source,
                        rationale: renameField.Rationale,
                        targetMember: renameField.Field);
                    var (newSolutionRf, csChangesRf) = await ApplyRenameAsync(solution, renameIntent, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRf;
                    docChanges.AddRange(csChangesRf);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case PullUpConstructorBodyIntent pucb:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, pucb.Subclass, out var cppTargetForPucb)
                        && cppTargetForPucb is not null)
                    {
                        var subName = ExtractShortName(pucb.Subclass.FullyQualifiedName);
                        var parentName = ExtractShortName(pucb.Parent.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.PullUpConstructorBody(cppTargetForPucb, subName, parentName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyPullUpConstructorBodyAsync(solution, pucb, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case EncapsulateFieldIntent encap:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, encap.OwnerType, out var cppTargetForEncap)
                        && cppTargetForEncap is not null)
                    {
                        var ownerName = ExtractShortName(encap.OwnerType.FullyQualifiedName);
                        var fieldName = ExtractMemberNameFromSignature(encap.Field.Signature);
                        var cppChanges = CppCliRefactorEngine.EncapsulateField(cppTargetForEncap, ownerName, fieldName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyEncapsulateFieldAsync(solution, encap, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case MoveMethodIntent moveM:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, moveM.SourceType, out var cppTargetForMm)
                        && cppTargetForMm is not null)
                    {
                        var srcName = ExtractShortName(moveM.SourceType.FullyQualifiedName);
                        var dstName = ExtractShortName(moveM.TargetType.FullyQualifiedName);
                        var memberNames = moveM.Members.Select(m => ExtractMemberNameFromSignature(m.Signature)).ToArray();
                        var cppChanges = CppCliRefactorEngine.MoveMembersBetweenClasses(cppTargetForMm, srcName, dstName, memberNames);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await MoveMembersBetweenClassesAsync(solution, moveM.SourceType, moveM.TargetType, moveM.Members, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case MoveFieldIntent moveF:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, moveF.SourceType, out var cppTargetForMf)
                        && cppTargetForMf is not null)
                    {
                        var srcName = ExtractShortName(moveF.SourceType.FullyQualifiedName);
                        var dstName = ExtractShortName(moveF.TargetType.FullyQualifiedName);
                        var memberNames = moveF.Members.Select(m => ExtractMemberNameFromSignature(m.Signature)).ToArray();
                        var cppChanges = CppCliRefactorEngine.MoveMembersBetweenClasses(cppTargetForMf, srcName, dstName, memberNames);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await MoveMembersBetweenClassesAsync(solution, moveF.SourceType, moveF.TargetType, moveF.Members, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceConstructorWithFactoryIntent factory:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, factory.OwnerType, out var cppTargetForFactory)
                        && cppTargetForFactory is not null)
                    {
                        var ownerName = ExtractShortName(factory.OwnerType.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.ReplaceConstructorWithFactory(cppTargetForFactory, ownerName, factory.FactoryName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyReplaceConstructorWithFactoryAsync(solution, factory, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceMagicNumberIntent magic:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, magic.OwnerType, out var cppTargetForMagic)
                        && cppTargetForMagic is not null)
                    {
                        var ownerName = ExtractShortName(magic.OwnerType.FullyQualifiedName);
                        var cppChanges = CppCliRefactorEngine.ReplaceMagicNumber(cppTargetForMagic, ownerName, magic.LiteralValue, magic.ConstantName, magic.ConstantType);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyReplaceMagicNumberAsync(solution, magic, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ChangeBidirectionalToUnidirectionalIntent unidir:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, unidir.OwnerType, out var cppTargetForUnidir)
                        && cppTargetForUnidir is not null)
                    {
                        var ownerName = ExtractShortName(unidir.OwnerType.FullyQualifiedName);
                        var fieldName = ExtractMemberNameFromSignature(unidir.Field.Signature);
                        var cppChanges = CppCliRefactorEngine.RemoveFieldFromClass(cppTargetForUnidir, ownerName, fieldName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var csChanges = await ApplyChangeBidirectionalToUnidirectionalAsync(solution, unidir, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case IntroduceParameterObjectIntent ipo:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, ipo.OwnerType, out _))
                    {
                        throw new NotSupportedException("Introduce Parameter Object is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionIpo, csChangesIpo) = await ApplyIntroduceParameterObjectAsync(solution, ipo, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionIpo;
                    docChanges.AddRange(csChangesIpo);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case AddParameterIntent addP:
                {
                    // Value inserted at each call site. DefaultValue if given, else `default`
                    // so callers keep compiling for both required and optional parameters.
                    var addPValueText = string.IsNullOrEmpty(addP.DefaultValue) ? "default" : addP.DefaultValue!;
                    ArgumentListSyntax? AddPRewriter(ArgumentListSyntax list) => AppendArgumentRewriter(list, addPValueText);

                    if (CppCliRefactorEngine.TryFindTargetByType(model, addP.OwnerType, out var cppTargetForAddP)
                        && cppTargetForAddP is not null)
                    {
                        // Cpp-owned method: text-patch the .h signature (Cpp side) and, in
                        // parallel, find + rewrite every C# call site of the same method.
                        var ownerName = ExtractShortName(addP.OwnerType.FullyQualifiedName);
                        var methodName = ExtractMemberNameFromSignature(addP.Method.Signature);
                        var paramDecl = string.IsNullOrEmpty(addP.DefaultValue)
                            ? $"{addP.ParameterType} {addP.ParameterName}"
                            : $"{addP.ParameterType} {addP.ParameterName} = {addP.DefaultValue}";

                        var cppTaskAddP = Task.Run(
                            () => CppCliRefactorEngine.AddParameterToMethod(cppTargetForAddP, ownerName, methodName, paramDecl),
                            cancellationToken);
                        var csCallSiteTaskAddP = BuildCSharpCallSiteRewriteAsync(addP.OwnerType, addP.Method, AddPRewriter, cancellationToken);
                        await Task.WhenAll(cppTaskAddP, csCallSiteTaskAddP).ConfigureAwait(false);
                        docChanges.AddRange(cppTaskAddP.Result);
                        docChanges.AddRange(csCallSiteTaskAddP.Result);
                        appliedIds.Add(intent.Id);
                        break;
                    }

                    // C#-owned method: rewrite declaration + all call sites in parallel.
                    var declTaskAddP = ApplyAddParameterAsync(solution, addP, cancellationToken);
                    var callSiteTaskAddP = BuildCSharpCallSiteRewriteAsync(addP.OwnerType, addP.Method, AddPRewriter, cancellationToken);
                    await Task.WhenAll(declTaskAddP, callSiteTaskAddP).ConfigureAwait(false);
                    docChanges.AddRange(declTaskAddP.Result);
                    docChanges.AddRange(callSiteTaskAddP.Result);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case RemoveParameterIntent rmP:
                {
                    // Position of the parameter within the method signature — needed to
                    // strip positional args at call sites (named args are handled by name).
                    var rmPIndex = TryExtractParameterIndex(rmP.Method.Signature, rmP.ParameterName);
                    ArgumentListSyntax? RmPRewriter(ArgumentListSyntax list) => RemoveArgumentRewriter(list, rmPIndex, rmP.ParameterName);

                    if (CppCliRefactorEngine.TryFindTargetByType(model, rmP.OwnerType, out var cppTargetForRmP)
                        && cppTargetForRmP is not null)
                    {
                        var ownerName = ExtractShortName(rmP.OwnerType.FullyQualifiedName);
                        var methodName = ExtractMemberNameFromSignature(rmP.Method.Signature);

                        var cppTaskRmP = Task.Run(
                            () => CppCliRefactorEngine.RemoveParameterFromMethod(cppTargetForRmP, ownerName, methodName, rmP.ParameterName),
                            cancellationToken);
                        var csCallSiteTaskRmP = BuildCSharpCallSiteRewriteAsync(rmP.OwnerType, rmP.Method, RmPRewriter, cancellationToken);
                        await Task.WhenAll(cppTaskRmP, csCallSiteTaskRmP).ConfigureAwait(false);
                        docChanges.AddRange(cppTaskRmP.Result);
                        docChanges.AddRange(csCallSiteTaskRmP.Result);
                        appliedIds.Add(intent.Id);
                        break;
                    }

                    var declTaskRmP = ApplyRemoveParameterAsync(solution, rmP, cancellationToken);
                    var callSiteTaskRmP = BuildCSharpCallSiteRewriteAsync(rmP.OwnerType, rmP.Method, RmPRewriter, cancellationToken);
                    await Task.WhenAll(declTaskRmP, callSiteTaskRmP).ConfigureAwait(false);
                    docChanges.AddRange(declTaskRmP.Result);
                    docChanges.AddRange(callSiteTaskRmP.Result);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceDataValueWithObjectIntent rdv:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, rdv.OwnerType, out _))
                    {
                        throw new NotSupportedException("Replace Data Value with Object is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionRdv, csChangesRdv) = await ApplyReplaceDataValueWithObjectAsync(solution, rdv, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRdv;
                    docChanges.AddRange(csChangesRdv);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case RenameParameterIntent rnP:
                {
                    // Cpp-defined target: semantic rewrite (Cpp) + SymbolFinder for C# named argument sites.
                    if (_cppCompilation is not null
                        && _cppCompilation.GetTypeByFullyQualifiedName(rnP.OwnerType.FullyQualifiedName) is { } cppTypeRnP)
                    {
                        var memberName = ExtractMemberNameFromSignature(rnP.Method.Signature);
                        var memberInfo = _cppCompilation.ResolveMember(cppTypeRnP, memberName);
                        var cppMemberRnP = memberInfo.Symbol ?? memberInfo.CandidateSymbols.FirstOrDefault();
                        if (cppMemberRnP is not null)
                        {
                            var cppChangesTaskRnP = Task.Run(
                                () => CppRenameEngine.RenameParameter(_cppCompilation, cppMemberRnP, rnP.OldName, rnP.NewName),
                                cancellationToken);
                            var csChangesTaskRnP = BuildCSharpParameterRenameChangesAsync(rnP, cancellationToken);
                            await Task.WhenAll(cppChangesTaskRnP, csChangesTaskRnP).ConfigureAwait(false);
                            var cppRnPChanges = cppChangesTaskRnP.Result;
                            if (cppRnPChanges.Count > 0)
                            {
                                docChanges.AddRange(cppRnPChanges);
                                docChanges.AddRange(csChangesTaskRnP.Result);
                                appliedIds.Add(intent.Id);
                                break;
                            }
                        }
                    }

                    if (CppCliRefactorEngine.TryFindTargetByType(model, rnP.OwnerType, out var cppTargetForRnP)
                        && cppTargetForRnP is not null)
                    {
                        var ownerName = ExtractShortName(rnP.OwnerType.FullyQualifiedName);
                        var methodName2 = ExtractMemberNameFromSignature(rnP.Method.Signature);
                        var cppChanges = CppCliRefactorEngine.RenameParameter(cppTargetForRnP, ownerName, methodName2, rnP.OldName, rnP.NewName);
                        docChanges.AddRange(cppChanges);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var (newSolutionRnP, csChangesRnP) = await ApplyRenameParameterAsync(solution, rnP, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRnP;
                    docChanges.AddRange(csChangesRnP);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case SelfEncapsulateFieldIntent sef:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, sef.OwnerType, out _))
                    {
                        throw new NotSupportedException("Self Encapsulate Field is not supported for C++/CLI targets yet.");
                    }
                    var csChanges = await ApplySelfEncapsulateFieldAsync(solution, sef, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ChangeReferenceToValueIntent crv:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, crv.OwnerType, out _))
                    {
                        throw new NotSupportedException("Change Reference to Value is not supported for C++/CLI targets yet.");
                    }
                    var csChanges = await ApplyChangeReferenceToValueAsync(solution, crv, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ChangeValueToReferenceIntent cvr:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, cvr.OwnerType, out _))
                    {
                        throw new NotSupportedException("Change Value to Reference is not supported for C++/CLI targets yet.");
                    }
                    var csChanges = await ApplyChangeValueToReferenceAsync(solution, cvr, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceTypeCodeWithClassIntent rtc:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, rtc.OwnerType, out _))
                    {
                        throw new NotSupportedException("Replace Type Code with Class is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionRtc, csChangesRtc) = await ApplyReplaceTypeCodeWithClassAsync(solution, rtc, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRtc;
                    docChanges.AddRange(csChangesRtc);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case PreserveWholeObjectIntent pwo:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, pwo.OwnerType, out _))
                    {
                        throw new NotSupportedException("Preserve Whole Object is not supported for C++/CLI targets yet.");
                    }
                    var csChanges = await ApplyPreserveWholeObjectAsync(solution, pwo, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceArrayWithObjectIntent rao:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, rao.OwnerType, out _))
                    {
                        throw new NotSupportedException("Replace Array with Object is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionRao, csChangesRao) = await ApplyReplaceArrayWithObjectAsync(solution, rao, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRao;
                    docChanges.AddRange(csChangesRao);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceTypeCodeWithSubclassesIntent rts:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, rts.OwnerType, out _))
                    {
                        throw new NotSupportedException("Replace Type Code with Subclasses is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionRts, csChangesRts) = await ApplyReplaceTypeCodeWithSubclassesAsync(solution, rts, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRts;
                    docChanges.AddRange(csChangesRts);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceSubclassWithFieldsIntent rsf:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, rsf.ParentType, out _))
                    {
                        throw new NotSupportedException("Replace Subclass with Fields is not supported for C++/CLI targets yet.");
                    }
                    var csChanges = await ApplyReplaceSubclassWithFieldsAsync(solution, rsf, cancellationToken).ConfigureAwait(false);
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ExtractHierarchyIntent eh:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, eh.OwnerType, out _))
                    {
                        throw new NotSupportedException("Extract Hierarchy is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionEh, csChangesEh) = await ApplyExtractHierarchyAsync(solution, eh, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionEh;
                    docChanges.AddRange(csChangesEh);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case TeaseApartInheritanceIntent tap:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, tap.PrimaryHierarchyRoot, out _))
                    {
                        throw new NotSupportedException("Tease Apart Inheritance is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionTap, csChangesTap) = await ApplyTeaseApartInheritanceAsync(solution, tap, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionTap;
                    docChanges.AddRange(csChangesTap);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ConvertProceduralToObjectsIntent cpo:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, cpo.ProceduralClass, out _)
                        || CppCliRefactorEngine.TryFindTargetByType(model, cpo.DataRecordType, out _))
                    {
                        throw new NotSupportedException("Convert Procedural to Objects is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionCpo, csChangesCpo) = await ApplyConvertProceduralToObjectsAsync(solution, cpo, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionCpo;
                    docChanges.AddRange(csChangesCpo);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ExtractMethodIntent em:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, em.OwnerType, out _))
                    {
                        throw new NotSupportedException("Extract Method is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionEm, csChangesEm) = await ApplyExtractMethodAsync(solution, em, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionEm;
                    docChanges.AddRange(csChangesEm);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ExtractVariableIntent ev:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, ev.OwnerType, out _))
                    {
                        throw new NotSupportedException("Extract Variable is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionEv, csChangesEv) = await ApplyExtractVariableAsync(solution, ev, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionEv;
                    docChanges.AddRange(csChangesEv);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case InlineMethodIntent im:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, im.OwnerType, out _))
                    {
                        throw new NotSupportedException("Inline Method is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionIm, csChangesIm) = await ApplyInlineMethodAsync(solution, im, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionIm;
                    docChanges.AddRange(csChangesIm);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case InlineVariableIntent iv:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, iv.OwnerType, out _))
                    {
                        throw new NotSupportedException("Inline Variable is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionIv, csChangesIv) = await ApplyInlineVariableAsync(solution, iv, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionIv;
                    docChanges.AddRange(csChangesIv);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case DecomposeConditionalIntent dc:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, dc.OwnerType, out _))
                    {
                        throw new NotSupportedException("Decompose Conditional is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionDc, csChangesDc) = await ApplyDecomposeConditionalAsync(solution, dc, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionDc;
                    docChanges.AddRange(csChangesDc);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ConsolidateConditionalExpressionIntent cce:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, cce.OwnerType, out _))
                    {
                        throw new NotSupportedException("Consolidate Conditional Expression is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionCce, csChangesCce) = await ApplyConsolidateConditionalExpressionAsync(solution, cce, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionCce;
                    docChanges.AddRange(csChangesCce);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ConsolidateDuplicateConditionalFragmentsIntent cdf:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, cdf.OwnerType, out _))
                    {
                        throw new NotSupportedException("Consolidate Duplicate Conditional Fragments is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionCdf, csChangesCdf) = await ApplyConsolidateDuplicateConditionalFragmentsAsync(solution, cdf, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionCdf;
                    docChanges.AddRange(csChangesCdf);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case ReplaceNestedConditionalWithGuardClausesIntent rng:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, rng.OwnerType, out _))
                    {
                        throw new NotSupportedException("Replace Nested Conditional with Guard Clauses is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionRng, csChangesRng) = await ApplyReplaceNestedConditionalWithGuardClausesAsync(solution, rng, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionRng;
                    docChanges.AddRange(csChangesRng);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case IntroduceNullObjectIntent ino:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, ino.SourceType, out _))
                    {
                        throw new NotSupportedException("Introduce Null Object is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionIno, csChangesIno) = await ApplyIntroduceNullObjectAsync(solution, ino, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionIno;
                    docChanges.AddRange(csChangesIno);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case IntroduceAssertionIntent ia:
                {
                    if (CppCliRefactorEngine.TryFindTargetByType(model, ia.OwnerType, out _))
                    {
                        throw new NotSupportedException("Introduce Assertion is not supported for C++/CLI targets yet.");
                    }
                    var (newSolutionIa, csChangesIa) = await ApplyIntroduceAssertionAsync(solution, ia, cancellationToken).ConfigureAwait(false);
                    solution = newSolutionIa;
                    docChanges.AddRange(csChangesIa);
                    appliedIds.Add(intent.Id);
                    break;
                }

                case AddGhostTypeIntent add:
                {
                    if (CppCliRefactorEngine.TryFindTargetByNamespace(model, add.Namespace, out var cppTarget)
                        && cppTarget is not null)
                    {
                        var changes = CppCliRefactorEngine.AddGhostType(cppTarget, add);
                        docChanges.AddRange(changes);
                        appliedIds.Add(intent.Id);
                        break;
                    }
                    var (newSolution, csChanges) = ApplyAddGhostType(solution, add);
                    solution = newSolution;
                    docChanges.AddRange(csChanges);
                    appliedIds.Add(intent.Id);
                    break;
                }

                default:
                    throw new NotSupportedException($"Intent type not yet supported: {intent.GetType().Name}");
            }
        }

        return new ChangeSet(
            AppliedIntentIds: appliedIds,
            Changes: docChanges,
            Summary: $"Proposed {appliedIds.Count} intent(s), {docChanges.Count} document change(s).");
    }

    public async Task<SolutionModel> ApplyChangesAsync(ChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        // 1) Write the changes to disk so external tooling (Visual Studio, msbuild, git)
        //    sees the same source the model does.
        foreach (var change in changeSet.Changes)
        {
            switch (change.Kind)
            {
                case DocumentChangeKind.Modified:
                case DocumentChangeKind.Added:
                    if (change.NewText is null || string.IsNullOrEmpty(change.FilePath))
                    {
                        continue;
                    }
                    var dir = Path.GetDirectoryName(change.FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await File.WriteAllTextAsync(change.FilePath, change.NewText, cancellationToken).ConfigureAwait(false);
                    break;

                case DocumentChangeKind.Deleted:
                    if (!string.IsNullOrEmpty(change.FilePath) && File.Exists(change.FilePath))
                    {
                        File.Delete(change.FilePath);
                    }
                    break;

                default:
                    throw new NotSupportedException($"DocumentChangeKind not yet supported: {change.Kind}");
            }
        }

        // 2) Update the Roslyn Solution in-place via immutable With* transforms — this
        //    is the big win over the old `_workspace.OpenSolutionAsync(slnPath)` reload,
        //    which was multi-second on large solutions. We never touch MSBuild here.
        if (_solution is not null)
        {
            var updated = _solution;
            foreach (var change in changeSet.Changes)
            {
                if (string.IsNullOrEmpty(change.FilePath)) continue;
                switch (change.Kind)
                {
                    case DocumentChangeKind.Modified when change.NewText is not null:
                        foreach (var docId in updated.GetDocumentIdsWithFilePath(change.FilePath))
                        {
                            updated = updated.WithDocumentText(
                                docId,
                                Microsoft.CodeAnalysis.Text.SourceText.From(change.NewText, Encoding.UTF8));
                        }
                        break;

                    case DocumentChangeKind.Added when change.NewText is not null:
                    {
                        var projectId = FindOwningProjectId(updated, change.FilePath);
                        if (projectId is not null)
                        {
                            var docInfo = DocumentInfo.Create(
                                DocumentId.CreateNewId(projectId),
                                Path.GetFileName(change.FilePath),
                                filePath: change.FilePath,
                                loader: TextLoader.From(TextAndVersion.Create(
                                    Microsoft.CodeAnalysis.Text.SourceText.From(change.NewText, Encoding.UTF8),
                                    VersionStamp.Create())));
                            updated = updated.AddDocument(docInfo);
                        }
                        break;
                    }

                    case DocumentChangeKind.Deleted:
                        foreach (var docId in updated.GetDocumentIdsWithFilePath(change.FilePath))
                        {
                            updated = updated.RemoveDocument(docId);
                        }
                        break;
                }
            }
            _solution = updated;
            // NOTE: _workspace.TryApplyChanges was tempting for symmetry with
            // CurrentSolution, but it goes through MSBuildWorkspace's project-system
            // path, which pulls in native MSBuild dependencies and has been observed
            // to throw DllNotFoundException on some hosts. The Cpp-shim MetadataReferences
            // it doesn't understand also get stripped, which then breaks binding and
            // MapAsync ends up producing a nearly-empty SolutionModel — the "everything
            // marked removed" symptom. _solution field alone is the source of truth for
            // us; other code that peeks at _workspace.CurrentSolution won't see our
            // apply, but no critical path does that today.
        }

        // 2.5) Cpp/CLI file が触られたら _cppCompilation と _lastForeignProjects を
        //      再構築する。ここを抜けると diff は書き込まれても diagram / smell / Ctrl+Click
        //      が旧状態のまま固まる。header / impl / vcxproj いずれの追加・変更・削除にも反応。
        if (_lastDiscoveredForeignProjects.Count > 0
            && changeSet.Changes.Any(c => TouchesCppFile(c.FilePath)))
        {
            _cppCompilation = BuildCppCompilation(_lastDiscoveredForeignProjects);
            _lastForeignProjects = BuildForeignProjectModels(_lastDiscoveredForeignProjects, _cppCompilation);
        }

        // 3) Rebuild the SolutionModel from the updated Roslyn Solution and merge the
        //    cached foreign (C++/CLI) projects back in — same shape LoadSolutionAsync
        //    returns. Without this the diff overlay flags every C++/CLI type as removed.
        if (_solution is null)
        {
            return new SolutionModel(string.Empty, _lastForeignProjects);
        }
        var managed = await MapAsync(_solution, cancellationToken).ConfigureAwait(false);
        return _lastForeignProjects.Count == 0
            ? managed
            : managed with { Projects = managed.Projects.Concat(_lastForeignProjects).ToList() };
    }

    // Cpp semantic (header / impl / vcxproj) を触るかどうか。拡張子ベースの緩い判定で十分。
    // 判定漏れがあると diagram が古いまま固まる、判定余りは追加の再パース (数百 ms) 程度で害小。
    private static bool TouchesCppFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hxx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hh", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectId? FindOwningProjectId(MsSolution solution, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return null;
        ProjectId? best = null;
        var bestLen = -1;
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            if (project.FilePath is null) continue;
            var pdir = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(pdir)) continue;
            if (dir.StartsWith(pdir, StringComparison.OrdinalIgnoreCase) && pdir.Length > bestLen)
            {
                best = project.Id;
                bestLen = pdir.Length;
            }
        }
        return best;
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
        _solution = null;
    }

    private static async Task<SolutionModel> MapAsync(MsSolution solution, CancellationToken cancellationToken)
    {
        // Roslyn Compilation is immutable / thread-safe for reads, and each
        // project's GetCompilationAsync internally memoizes with its dependency
        // graph — so we can fan out per project. MapProject is CPU-bound
        // symbol walking, so we punt it to the ThreadPool so multiple projects
        // materialize concurrently instead of one at a time.
        var csharpProjects = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .ToList();

        var tasks = csharpProjects.Select(async project =>
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null) return null;
            return await Task.Run(
                () => RoslynToModelMapper.MapProject(project, compilation),
                cancellationToken).ConfigureAwait(false);
        });

        var mapped = await Task.WhenAll(tasks).ConfigureAwait(false);
        var projects = mapped.Where(p => p is not null).Cast<ProjectModel>().ToList();

        return new SolutionModel(solution.FilePath ?? string.Empty, projects);
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyRenameAsync(
        MsSolution solution,
        RenameIntent rename,
        CancellationToken cancellationToken)
    {
        var symbol = await SymbolResolver.ResolveAsync(solution, rename.TargetType, rename.TargetMember, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            var member = rename.TargetMember?.ToString() ?? "<type>";
            throw new InvalidOperationException($"Symbol not found for rename: {rename.TargetType} / {member}");
        }

        var options = new SymbolRenameOptions();
        var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, rename.NewName, cancellationToken).ConfigureAwait(false);

        return (newSolution, await CollectDocumentChangesAsync(solution, newSolution, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyExtractInterfaceAsync(
        MsSolution solution,
        ExtractInterfaceIntent intent,
        CancellationToken cancellationToken)
    {
        var typeSymbol = await SymbolResolver.ResolveAsync(solution, intent.SourceType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol;
        if (typeSymbol is null)
        {
            throw new InvalidOperationException($"Source type not found: {intent.SourceType}");
        }

        var expectedSignatures = new HashSet<string>(intent.Members.Select(m => m.Signature), StringComparer.Ordinal);
        var members = typeSymbol.GetMembers()
            .Where(m => expectedSignatures.Contains(RoslynToModelMapper.ToMemberRef(m).Signature))
            .ToList();
        if (members.Count == 0)
        {
            throw new InvalidOperationException($"No matching members found on {intent.SourceType} for the requested signatures.");
        }

        var classSyntaxRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.SourceType} has no source declaration.");
        var classFilePath = classSyntaxRef.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(classFilePath))
        {
            throw new InvalidOperationException($"{intent.SourceType} has no filesystem path.");
        }

        var classDocument = solution.GetDocument(classSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.SourceType} not found in solution.");

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (typeSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : typeSymbol.ContainingNamespace.ToDisplayString());

        var interfaceText = BuildInterfaceSource(namespaceName, intent.ProposedInterfaceName, members);
        var interfaceFilePath = Path.Combine(
            Path.GetDirectoryName(classFilePath)!,
            $"{intent.ProposedInterfaceName}.cs");

        var (updatedSolution, modifiedClassChange) = await AddInterfaceToBaseListAsync(
            solution,
            classDocument,
            typeSymbol,
            intent.ProposedInterfaceName,
            cancellationToken).ConfigureAwait(false);

        var interfaceDocument = updatedSolution
            .GetProject(classDocument.Project.Id)!
            .AddDocument(
                name: $"{intent.ProposedInterfaceName}.cs",
                text: interfaceText,
                folders: classDocument.Folders,
                filePath: interfaceFilePath);

        var finalSolution = interfaceDocument.Project.Solution;

        var changes = new List<DocumentChange>();
        if (modifiedClassChange is not null)
        {
            changes.Add(modifiedClassChange);
        }
        changes.Add(new DocumentChange(
            FilePath: interfaceFilePath,
            Kind: DocumentChangeKind.Added,
            OldText: null,
            NewText: interfaceText));

        return (finalSolution, changes);
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyExtractSuperclassAsync(
        MsSolution solution,
        ExtractSuperclassIntent intent,
        CancellationToken cancellationToken)
    {
        var typeSymbol = await SymbolResolver.ResolveAsync(solution, intent.SourceType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol;
        if (typeSymbol is null)
        {
            throw new InvalidOperationException($"Source type not found: {intent.SourceType}");
        }

        var expectedSignatures = new HashSet<string>(intent.Members.Select(m => m.Signature), StringComparer.Ordinal);
        var memberSymbols = typeSymbol.GetMembers()
            .Where(m => expectedSignatures.Contains(RoslynToModelMapper.ToMemberRef(m).Signature))
            .ToList();
        if (memberSymbols.Count == 0)
        {
            throw new InvalidOperationException(
                $"No matching members found on {intent.SourceType} for the requested signatures.");
        }

        var classSyntaxRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.SourceType} has no source declaration.");
        var classFilePath = classSyntaxRef.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(classFilePath))
        {
            throw new InvalidOperationException($"{intent.SourceType} has no filesystem path.");
        }

        var classDocument = solution.GetDocument(classSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.SourceType} not found in solution.");

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (typeSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : typeSymbol.ContainingNamespace.ToDisplayString());

        // Collect member syntax nodes with their bodies (unlike Extract Interface which just needs signatures).
        var memberSyntaxNodes = new List<MemberDeclarationSyntax>();
        foreach (var sym in memberSymbols)
        {
            foreach (var sref in sym.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (node is MemberDeclarationSyntax mds)
                {
                    memberSyntaxNodes.Add(mds);
                }
            }
        }

        var superText = BuildSuperclassSource(namespaceName, intent.ProposedSuperclassName, memberSymbols, memberSyntaxNodes);
        var superFilePath = Path.Combine(
            Path.GetDirectoryName(classFilePath)!,
            $"{intent.ProposedSuperclassName}.cs");

        var (updatedSolution, modifiedClassChange) = await RemoveMembersAndAddBaseAsync(
            solution,
            classDocument,
            typeSymbol,
            memberSymbols,
            intent.ProposedSuperclassName,
            cancellationToken).ConfigureAwait(false);

        var superDocument = updatedSolution
            .GetProject(classDocument.Project.Id)!
            .AddDocument(
                name: $"{intent.ProposedSuperclassName}.cs",
                text: superText,
                folders: classDocument.Folders,
                filePath: superFilePath);

        var finalSolution = superDocument.Project.Solution;

        var changes = new List<DocumentChange>();
        if (modifiedClassChange is not null)
        {
            changes.Add(modifiedClassChange);
        }
        changes.Add(new DocumentChange(
            FilePath: superFilePath,
            Kind: DocumentChangeKind.Added,
            OldText: null,
            NewText: superText));

        return (finalSolution, changes);
    }

    private static string BuildSuperclassSource(
        string namespaceName,
        string superclassName,
        IReadOnlyList<ISymbol> memberSymbols,
        IReadOnlyList<MemberDeclarationSyntax> memberNodes)
    {
        var sb = new StringBuilder();
        var hasNamespace = !string.IsNullOrEmpty(namespaceName);

        var usings = CollectUsedNamespaces(memberSymbols, namespaceName);
        foreach (var ns in usings)
        {
            sb.Append("using ").Append(ns).AppendLine(";");
        }
        if (usings.Count > 0)
        {
            sb.AppendLine();
        }

        if (hasNamespace)
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append("public abstract class ").AppendLine(superclassName);
        sb.AppendLine("{");
        foreach (var node in memberNodes)
        {
            var normalized = node.NormalizeWhitespace(indentation: "    ", eol: "\n");
            sb.Append("    ").Append(normalized.ToFullString()).AppendLine();
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static async Task<(MsSolution NewSolution, DocumentChange? Change)> RemoveMembersAndAddBaseAsync(
        MsSolution solution,
        Document classDocument,
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ISymbol> memberSymbols,
        string superclassName,
        CancellationToken cancellationToken)
    {
        var root = await classDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Source root unavailable.");
        var oldText = (await classDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var classNode = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == typeSymbol.Name);
        if (classNode is null)
        {
            return (solution, null);
        }

        var memberNodesToRemove = new HashSet<SyntaxNode>();
        foreach (var sym in memberSymbols)
        {
            foreach (var sref in sym.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (node is MemberDeclarationSyntax mds && classNode.Contains(mds))
                {
                    memberNodesToRemove.Add(mds);
                }
            }
        }

        var newClassNode = classNode.RemoveNodes(
            classNode.Members.Where(m => memberNodesToRemove.Contains(m)),
            SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepEndOfLine) ?? classNode;

        var newBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(superclassName));
        BaseListSyntax newBaseList = newClassNode.BaseList is null
            ? SyntaxFactory.BaseList(
                SyntaxFactory.Token(SyntaxKind.ColonToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(newBaseType))
            : newClassNode.BaseList.AddTypes(newBaseType);

        newClassNode = newClassNode
            .WithIdentifier(newClassNode.Identifier.WithTrailingTrivia(SyntaxFactory.Space))
            .WithBaseList(newBaseList)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        var newDoc = classDocument.WithSyntaxRoot(newRoot);
        var formattedDoc = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formattedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var change = new DocumentChange(
            FilePath: classDocument.FilePath ?? string.Empty,
            Kind: DocumentChangeKind.Modified,
            OldText: oldText,
            NewText: newText);

        return (formattedDoc.Project.Solution, change);
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyExtractClassAsync(
        MsSolution solution,
        ExtractClassIntent intent,
        CancellationToken cancellationToken)
    {
        var typeSymbol = await SymbolResolver.ResolveAsync(solution, intent.SourceType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol;
        if (typeSymbol is null)
        {
            throw new InvalidOperationException($"Source type not found: {intent.SourceType}");
        }

        var expectedSignatures = new HashSet<string>(intent.Members.Select(m => m.Signature), StringComparer.Ordinal);
        var memberSymbols = typeSymbol.GetMembers()
            .Where(m => expectedSignatures.Contains(RoslynToModelMapper.ToMemberRef(m).Signature))
            .ToList();
        if (memberSymbols.Count == 0)
        {
            throw new InvalidOperationException(
                $"No matching members found on {intent.SourceType} for the requested signatures.");
        }

        var classSyntaxRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.SourceType} has no source declaration.");
        var classFilePath = classSyntaxRef.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(classFilePath))
        {
            throw new InvalidOperationException($"{intent.SourceType} has no filesystem path.");
        }

        var classDocument = solution.GetDocument(classSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.SourceType} not found in solution.");

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (typeSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : typeSymbol.ContainingNamespace.ToDisplayString());

        var memberSyntaxNodes = new List<MemberDeclarationSyntax>();
        foreach (var sym in memberSymbols)
        {
            foreach (var sref in sym.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (node is MemberDeclarationSyntax mds)
                {
                    memberSyntaxNodes.Add(mds);
                }
            }
        }

        // 移動先クラスが元クラスの (移動しない) member を参照している場合、抽出後 compile 不能に
        // なる (元クラスの member にアクセスできない)。事前検出して分かりやすい error を返す。
        // 現状の Extract Class は「延ばし棒 (back-reference param)」までは自動生成しないので、
        // ユーザーには依存 member を追加選択するか、参照している moved member を除外するよう促す。
        var movedSymbolSet = new HashSet<ISymbol>(memberSymbols, SymbolEqualityComparer.Default);
        var crossRefs = await DetectCrossReferencesToStayingMembersAsync(
            classDocument, typeSymbol, memberSyntaxNodes, movedSymbolSet, cancellationToken).ConfigureAwait(false);
        if (crossRefs.Count > 0)
        {
            var lines = crossRefs
                .GroupBy(r => r.MovedMember.Name)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var names = string.Join(", ", g.Select(r => r.ReferencedMember.Name).Distinct().OrderBy(n => n));
                    return $"  {g.Key} 内で参照: {names}";
                });
            throw new InvalidOperationException(
                $"Cannot extract these members: they reference {crossRefs.Select(r => r.ReferencedMember.Name).Distinct().Count()} " +
                $"member(s) of {typeSymbol.Name} that would remain in the source class. " +
                $"Either add those members to the extraction, or unselect the referencing members.\n" +
                string.Join("\n", lines));
        }

        var newClassText = BuildExtractedClassSource(namespaceName, intent.ProposedClassName, memberSymbols, memberSyntaxNodes);
        var newClassFilePath = Path.Combine(
            Path.GetDirectoryName(classFilePath)!,
            $"{intent.ProposedClassName}.cs");

        var (updatedSolution, modifiedClassChange) = await RemoveMembersAndAddDelegatePropertyAsync(
            solution,
            classDocument,
            typeSymbol,
            memberSymbols,
            intent.ProposedClassName,
            intent.DelegatePropertyName,
            cancellationToken).ConfigureAwait(false);

        var newClassDocument = updatedSolution
            .GetProject(classDocument.Project.Id)!
            .AddDocument(
                name: $"{intent.ProposedClassName}.cs",
                text: newClassText,
                folders: classDocument.Folders,
                filePath: newClassFilePath);

        var finalSolution = newClassDocument.Project.Solution;

        var changes = new List<DocumentChange>();
        if (modifiedClassChange is not null)
        {
            changes.Add(modifiedClassChange);
        }
        changes.Add(new DocumentChange(
            FilePath: newClassFilePath,
            Kind: DocumentChangeKind.Added,
            OldText: null,
            NewText: newClassText));

        return (finalSolution, changes);
    }

    private readonly record struct CrossRef(ISymbol MovedMember, ISymbol ReferencedMember);

    // 元クラス側で「移動 member を裸で呼んでる identifier」を "delegateName.MovedMember" に rewrite する。
    // 削除される member 自身の内部 (自己参照) は対象外 (削除されるので rewrite しても無意味 + noise)。
    // nameof(...) の中は semantic-preserving で無視 (rewrite すると nameof の値が変わる)。
    private sealed class DelegatePrefixRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _sm;
        private readonly HashSet<ISymbol> _movedSymbols;
        private readonly string _delegateName;
        private readonly HashSet<SyntaxNode> _skipInsideThese;

        public DelegatePrefixRewriter(SemanticModel sm, HashSet<ISymbol> movedSymbols, string delegateName, HashSet<SyntaxNode> skipInside)
        {
            _sm = sm;
            _movedSymbols = movedSymbols;
            _delegateName = delegateName;
            _skipInsideThese = skipInside;
        }

        private bool IsInsideSkipRegion(SyntaxNode node)
        {
            foreach (var skip in _skipInsideThese)
            {
                if (skip.Contains(node)) return true;
            }
            return false;
        }

        private bool IsInsideNameOf(SyntaxNode node)
        {
            for (var p = node.Parent; p is not null; p = p.Parent)
            {
                if (p is InvocationExpressionSyntax inv
                    && inv.Expression is IdentifierNameSyntax id
                    && id.Identifier.ValueText == "nameof")
                {
                    return true;
                }
            }
            return false;
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // "this.MovedMethod(...)" → "_delegate.MovedMethod(...)"
            if (node.Expression is ThisExpressionSyntax
                && !IsInsideSkipRegion(node)
                && !IsInsideNameOf(node))
            {
                var info = _sm.GetSymbolInfo(node.Name);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is not null && _movedSymbols.Contains(symbol))
                {
                    return node.WithExpression(SyntaxFactory.IdentifierName(_delegateName));
                }
            }
            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            // 裸の identifier "MovedMethod(...)" → "_delegate.MovedMethod(...)"
            // ただし MemberAccessExpression の右辺 (foo.MovedMethod) は VisitMemberAccessExpression 側で
            // 処理 (this の場合のみ rewrite) するので、ここでは自分が親の左辺・トップレベル identifier のみ扱う。
            if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node) return base.VisitIdentifierName(node);
            if (node.Parent is NameEqualsSyntax) return base.VisitIdentifierName(node);   // property init in obj initializer
            if (node.Parent is QualifiedNameSyntax) return base.VisitIdentifierName(node);
            if (IsInsideSkipRegion(node)) return base.VisitIdentifierName(node);
            if (IsInsideNameOf(node)) return base.VisitIdentifierName(node);

            var info = _sm.GetSymbolInfo(node);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is null || !_movedSymbols.Contains(symbol)) return base.VisitIdentifierName(node);

            // static member への裸参照は _delegate. で呼ぶと意味が変わる (static context を失う)。今回は
            // Extract Class の対象がだいたい instance member なので skip 判定は緩めに instance のみ rewrite。
            if (symbol.IsStatic) return base.VisitIdentifierName(node);

            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(_delegateName),
                node.WithoutTrivia())
                .WithTriviaFrom(node);
        }
    }

    // 元 class の member SyntaxNode を rewrite 後の tree で見つける。
    // MemberDeclarationSyntax の Identifier + Kind + Parameter count で一致判定 (span 変わってる可能性ある)。
    private static SyntaxNode? FindEquivalentMember(ClassDeclarationSyntax rewrittenClass, SyntaxNode oldMember)
    {
        var oldName = MemberIdentifier(oldMember);
        var oldKind = oldMember.Kind();
        var oldParamCount = MemberParamCount(oldMember);
        foreach (var m in rewrittenClass.Members)
        {
            if (m.Kind() != oldKind) continue;
            if (MemberIdentifier(m) != oldName) continue;
            if (MemberParamCount(m) != oldParamCount) continue;
            return m;
        }
        return null;

        static string MemberIdentifier(SyntaxNode m) => m switch
        {
            MethodDeclarationSyntax md => md.Identifier.ValueText,
            PropertyDeclarationSyntax pd => pd.Identifier.ValueText,
            FieldDeclarationSyntax fd => fd.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText ?? "",
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText ?? "",
            _ => m.ToString(),
        };
        static int MemberParamCount(SyntaxNode m) => m switch
        {
            MethodDeclarationSyntax md => md.ParameterList.Parameters.Count,
            _ => -1,
        };
    }

    // 移動対象 member (memberSyntaxNodes) の body / initializer を semantic 解析し、
    // 「元 type の member を参照しているが、その参照先は移動対象に含まれていない」箇所を列挙。
    // 移動後の compile error を事前検出するのに使う。
    private static async Task<List<CrossRef>> DetectCrossReferencesToStayingMembersAsync(
        Document classDocument,
        INamedTypeSymbol sourceType,
        IReadOnlyList<MemberDeclarationSyntax> movedNodes,
        HashSet<ISymbol> movedSymbols,
        CancellationToken cancellationToken)
    {
        var results = new List<CrossRef>();
        var semanticModel = await classDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null) return results;

        foreach (var movedNode in movedNodes)
        {
            var movingMemberSymbol = semanticModel.GetDeclaredSymbol(movedNode, cancellationToken);
            if (movingMemberSymbol is null) continue;

            // Constructor / accessor などの下層まで含めて全部の name reference を確認する。
            foreach (var name in movedNode.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                // IdentifierNameSyntax や GenericNameSyntax 全部。
                // MemberAccessExpression の右辺 (foo.Bar) は Bar が SimpleNameSyntax として拾える。
                var info = semanticModel.GetSymbolInfo(name, cancellationToken);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is null) continue;

                // ContainingType が元 type と同じなら「元 type の member 参照」。
                var containing = symbol.ContainingType;
                if (containing is null || !SymbolEqualityComparer.Default.Equals(containing, sourceType)) continue;

                // 移動対象 (自分自身 or 他の moved member) への参照は問題ないので skip。
                if (movedSymbols.Contains(symbol)) continue;

                // static / const / nested type などは移動先でも直接呼べる (SourceType.Foo で書けば)。
                // ただし現状の rewrite はしないので、簡易的には全部 cross-ref 扱いにする。
                results.Add(new CrossRef(movingMemberSymbol, symbol));
            }
        }
        return results;
    }

    private static string BuildExtractedClassSource(
        string namespaceName,
        string className,
        IReadOnlyList<ISymbol> memberSymbols,
        IReadOnlyList<MemberDeclarationSyntax> memberNodes)
    {
        var sb = new StringBuilder();
        var hasNamespace = !string.IsNullOrEmpty(namespaceName);

        var usings = CollectUsedNamespaces(memberSymbols, namespaceName);
        foreach (var ns in usings)
        {
            sb.Append("using ").Append(ns).AppendLine(";");
        }
        if (usings.Count > 0)
        {
            sb.AppendLine();
        }

        if (hasNamespace)
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append("public class ").AppendLine(className);
        sb.AppendLine("{");
        foreach (var node in memberNodes)
        {
            var normalized = node.NormalizeWhitespace(indentation: "    ", eol: "\n");
            sb.Append("    ").Append(normalized.ToFullString()).AppendLine();
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static async Task<(MsSolution NewSolution, DocumentChange? Change)> RemoveMembersAndAddDelegatePropertyAsync(
        MsSolution solution,
        Document classDocument,
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ISymbol> memberSymbols,
        string newClassName,
        string delegatePropertyName,
        CancellationToken cancellationToken)
    {
        var root = await classDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Source root unavailable.");
        var oldText = (await classDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var classNode = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == typeSymbol.Name);
        if (classNode is null)
        {
            return (solution, null);
        }

        var memberNodesToRemove = new HashSet<SyntaxNode>();
        foreach (var sym in memberSymbols)
        {
            foreach (var sref in sym.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (node is MemberDeclarationSyntax mds && classNode.Contains(mds))
                {
                    memberNodesToRemove.Add(mds);
                }
            }
        }

        // 移動される member への元クラス側からの参照 (call site) を "_delegate.MovedMember" に
        // rewrite する (Bug B 対策)。member 削除の前にやらないと semantic 解析できない。
        var movedSymbolSet = new HashSet<ISymbol>(memberSymbols, SymbolEqualityComparer.Default);
        var semanticModel = await classDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is not null)
        {
            var rewriter = new DelegatePrefixRewriter(semanticModel, movedSymbolSet, delegatePropertyName, memberNodesToRemove);
            var rewrittenClassNode = (ClassDeclarationSyntax)rewriter.Visit(classNode);
            // rewrite で node tree が copy されるので、削除対象の SyntaxNode 参照を新 tree のものに差し替える。
            var newRemoveSet = new HashSet<SyntaxNode>();
            foreach (var oldNode in memberNodesToRemove)
            {
                var replacement = FindEquivalentMember(rewrittenClassNode, oldNode);
                if (replacement is not null) newRemoveSet.Add(replacement);
            }
            memberNodesToRemove = newRemoveSet;
            classNode = rewrittenClassNode;
        }

        var newClassNode = classNode.RemoveNodes(
            classNode.Members.Where(m => memberNodesToRemove.Contains(m)),
            SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepEndOfLine) ?? classNode;

        var delegateDeclText = $"public {newClassName} {delegatePropertyName} {{ get; }} = new {newClassName}();";
        var delegateDecl = SyntaxFactory.ParseMemberDeclaration(delegateDeclText)
            ?? throw new InvalidOperationException($"Failed to parse delegate property: {delegateDeclText}");
        delegateDecl = delegateDecl.WithAdditionalAnnotations(Formatter.Annotation);

        newClassNode = newClassNode
            .AddMembers(delegateDecl)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        var newDoc = classDocument.WithSyntaxRoot(newRoot);
        var formattedDoc = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formattedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var change = new DocumentChange(
            FilePath: classDocument.FilePath ?? string.Empty,
            Kind: DocumentChangeKind.Modified,
            OldText: oldText,
            NewText: newText);

        return (formattedDoc.Project.Solution, change);
    }

    private static async Task<List<DocumentChange>> ApplyCollapseHierarchyAsync(
        MsSolution solution,
        CollapseHierarchyIntent intent,
        CancellationToken cancellationToken)
    {
        var subSymbol = await SymbolResolver.ResolveAsync(solution, intent.Subclass, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Subclass not found: {intent.Subclass}");
        var parentSymbol = await SymbolResolver.ResolveAsync(solution, intent.Parent, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Parent not found: {intent.Parent}");

        var subSyntaxRef = subSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.Subclass} has no source declaration.");
        var subDoc = solution.GetDocument(subSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.Subclass} not found.");
        var subFilePath = subDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.Subclass} has no filesystem path.");
        var subFileText = (await subDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var parentSyntaxRef = parentSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.Parent} has no source declaration.");
        var parentDoc = solution.GetDocument(parentSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.Parent} not found.");
        var parentFilePath = parentDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.Parent} has no filesystem path.");

        // Collect member syntax nodes from the subclass.
        var subMemberNodes = new List<MemberDeclarationSyntax>();
        foreach (var sref in subSymbol.DeclaringSyntaxReferences)
        {
            var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            if (node is ClassDeclarationSyntax cds)
            {
                subMemberNodes.AddRange(cds.Members);
            }
        }

        // Add those members onto the parent class node.
        var parentRoot = await parentDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Parent source root unavailable.");
        var parentOldText = (await parentDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        var parentClassNode = parentRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == parentSymbol.Name);
        if (parentClassNode is null)
        {
            throw new InvalidOperationException($"Parent class node for {parentSymbol.Name} not located in {parentFilePath}.");
        }

        var mergedParentNode = parentClassNode
            .AddMembers(subMemberNodes.ToArray())
            .WithAdditionalAnnotations(Formatter.Annotation);
        var mergedRoot = parentRoot.ReplaceNode(parentClassNode, mergedParentNode);
        var mergedDoc = parentDoc.WithSyntaxRoot(mergedRoot);
        var formattedDoc = await Formatter.FormatAsync(mergedDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var mergedText = (await formattedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        // Any leftover reference to SubName inside the merged parent text should also become ParentName.
        var subName = ExtractShortNameLocal(intent.Subclass.FullyQualifiedName);
        var parentName = ExtractShortNameLocal(intent.Parent.FullyQualifiedName);
        var replace = new System.Text.RegularExpressions.Regex(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(subName)}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var mergedTextRewritten = replace.Replace(mergedText, parentName);

        var changes = new List<DocumentChange>
        {
            new(parentFilePath, DocumentChangeKind.Modified, OldText: parentOldText, NewText: mergedTextRewritten),
            new(subFilePath, DocumentChangeKind.Deleted, OldText: subFileText, NewText: null),
        };

        // Textual replace for every other C# document.
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            foreach (var doc in project.Documents)
            {
                var path = doc.FilePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(path, subFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(path, parentFilePath, StringComparison.OrdinalIgnoreCase)) continue;

                string original;
                try { original = File.ReadAllText(path); }
                catch { continue; }

                var updated = replace.Replace(original, parentName);
                if (!string.Equals(original, updated, StringComparison.Ordinal))
                {
                    changes.Add(new DocumentChange(path, DocumentChangeKind.Modified, OldText: original, NewText: updated));
                }
            }
        }

        return changes;
    }

    private static readonly System.Text.RegularExpressions.Regex CachedIdentifierBoundary =
        new(@"\A[A-Za-z0-9_]+\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<DocumentChange> ApplyRemoveSubclass(MsSolution solution, RemoveSubclassIntent intent)
    {
        var subName = ExtractShortNameLocal(intent.Subclass.FullyQualifiedName);
        var baseName = ExtractShortNameLocal(intent.ReplacementBase.FullyQualifiedName);
        if (!CachedIdentifierBoundary.IsMatch(subName) || !CachedIdentifierBoundary.IsMatch(baseName))
        {
            throw new InvalidOperationException($"Non-identifier short name: sub={subName} base={baseName}");
        }

        var pattern = new System.Text.RegularExpressions.Regex(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(subName)}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var changes = new List<DocumentChange>();
        string? subclassFilePath = null;

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            foreach (var doc in project.Documents)
            {
                var path = doc.FilePath;
                if (string.IsNullOrEmpty(path)) continue;
                string original;
                try { original = File.ReadAllText(path); }
                catch { continue; }

                var updated = pattern.Replace(original, baseName);
                if (string.Equals(original, updated, StringComparison.Ordinal))
                {
                    continue;
                }

                // Is this file the subclass definition file? If so we'll delete it wholesale
                // rather than emit a Modified change (whose contents would introduce a
                // duplicate class named `baseName`).
                if (subclassFilePath is null
                    && ContainsSubclassDeclaration(original, subName))
                {
                    subclassFilePath = path;
                    continue;
                }

                changes.Add(new DocumentChange(path, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }

        if (subclassFilePath is not null)
        {
            changes.Add(new DocumentChange(subclassFilePath, DocumentChangeKind.Deleted, OldText: File.ReadAllText(subclassFilePath), NewText: null));
        }

        return changes;
    }

    private static bool ContainsSubclassDeclaration(string source, string subName)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            $@"\bclass\s+{System.Text.RegularExpressions.Regex.Escape(subName)}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        return pattern.IsMatch(source);
    }

    private static string ExtractShortNameLocal(string fullyQualifiedName)
    {
        var lastDot = fullyQualifiedName.LastIndexOf('.');
        return lastDot < 0 ? fullyQualifiedName : fullyQualifiedName[(lastDot + 1)..];
    }

    private static async Task<List<DocumentChange>> MoveMembersBetweenClassesAsync(
        MsSolution solution,
        TypeRef sourceType,
        TypeRef targetType,
        IReadOnlyList<MemberRef> members,
        CancellationToken cancellationToken)
    {
        var sourceSymbol = await SymbolResolver.ResolveAsync(solution, sourceType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Source type not found: {sourceType}");
        var targetSymbol = await SymbolResolver.ResolveAsync(solution, targetType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Target type not found: {targetType}");

        var expectedSignatures = new HashSet<string>(members.Select(m => m.Signature), StringComparer.Ordinal);
        var memberSymbols = sourceSymbol.GetMembers()
            .Where(m => expectedSignatures.Contains(RoslynToModelMapper.ToMemberRef(m).Signature))
            .ToList();
        if (memberSymbols.Count == 0)
        {
            throw new InvalidOperationException($"No matching members found on {sourceType}.");
        }

        var sourceSyntaxRef = sourceSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{sourceType} has no source declaration.");
        var sourceDoc = solution.GetDocument(sourceSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {sourceType} not found.");
        var sourceFilePath = sourceDoc.FilePath
            ?? throw new InvalidOperationException($"{sourceType} has no filesystem path.");
        var sourceOldText = (await sourceDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var targetSyntaxRef = targetSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{targetType} has no source declaration.");
        var targetDoc = solution.GetDocument(targetSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {targetType} not found.");
        var targetFilePath = targetDoc.FilePath
            ?? throw new InvalidOperationException($"{targetType} has no filesystem path.");

        var memberNodes = new List<MemberDeclarationSyntax>();
        foreach (var sym in memberSymbols)
        {
            foreach (var sref in sym.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                var mds = node as MemberDeclarationSyntax ?? node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                if (mds is not null) memberNodes.Add(mds);
            }
        }

        var sameDoc = string.Equals(sourceFilePath, targetFilePath, StringComparison.OrdinalIgnoreCase);
        var changes = new List<DocumentChange>();

        if (sameDoc)
        {
            var root = await sourceDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Source root unavailable.");
            var sourceClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.ValueText == sourceSymbol.Name)
                ?? throw new InvalidOperationException($"Source class {sourceSymbol.Name} not found.");
            var targetClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.ValueText == targetSymbol.Name)
                ?? throw new InvalidOperationException($"Target class {targetSymbol.Name} not found.");

            var toRemove = sourceClass.Members.Where(m => memberNodes.Contains(m)).ToList();
            var extractedClones = toRemove
                .Select(m => m.NormalizeWhitespace(indentation: "    ", eol: "\n"))
                .ToArray();

            var targetAnnotation = new SyntaxAnnotation("moveMembersTarget");
            var annotatedTarget = targetClass.WithAdditionalAnnotations(targetAnnotation);
            var rootStep1 = root.ReplaceNode(targetClass, annotatedTarget);

            var sourceInStep1 = rootStep1.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .First(c => c.Identifier.ValueText == sourceSymbol.Name);
            var toRemoveInStep1 = sourceInStep1.Members
                .Where(m => toRemove.Any(tr => tr.Span == m.Span))
                .ToList();
            var newSourceClass = sourceInStep1.RemoveNodes(toRemoveInStep1,
                SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepEndOfLine) ?? sourceInStep1;
            var rootStep2 = rootStep1.ReplaceNode(sourceInStep1, newSourceClass);

            var targetInStep2 = rootStep2.GetAnnotatedNodes(targetAnnotation)
                .OfType<ClassDeclarationSyntax>().First();
            var newTargetClass = targetInStep2.AddMembers(extractedClones)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newRoot = rootStep2.ReplaceNode(targetInStep2, newTargetClass);

            var newDoc = sourceDoc.WithSyntaxRoot(newRoot);
            var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

            changes.Add(new DocumentChange(sourceFilePath, DocumentChangeKind.Modified, sourceOldText, newText));
        }
        else
        {
            var targetOldText = (await targetDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

            var sourceRoot = await sourceDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("Source root unavailable.");
            var sourceClass = sourceRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.ValueText == sourceSymbol.Name)
                ?? throw new InvalidOperationException($"Source class {sourceSymbol.Name} not found.");
            var toRemove = sourceClass.Members.Where(m => memberNodes.Contains(m)).ToList();
            var extractedClones = toRemove
                .Select(m => m.NormalizeWhitespace(indentation: "    ", eol: "\n"))
                .ToArray();
            var newSourceClass = sourceClass.RemoveNodes(toRemove,
                SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepEndOfLine) ?? sourceClass;
            var newSourceRoot = sourceRoot.ReplaceNode(sourceClass, newSourceClass);
            var newSourceDoc = sourceDoc.WithSyntaxRoot(newSourceRoot);
            var newSourceFormatted = await Formatter.FormatAsync(newSourceDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newSourceText = (await newSourceFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            changes.Add(new DocumentChange(sourceFilePath, DocumentChangeKind.Modified, sourceOldText, newSourceText));

            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("Target root unavailable.");
            var targetClass = targetRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.ValueText == targetSymbol.Name)
                ?? throw new InvalidOperationException($"Target class {targetSymbol.Name} not found.");
            var newTargetClass = targetClass.AddMembers(extractedClones)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newTargetRoot = targetRoot.ReplaceNode(targetClass, newTargetClass);
            var newTargetDoc = targetDoc.WithSyntaxRoot(newTargetRoot);
            var newTargetFormatted = await Formatter.FormatAsync(newTargetDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newTargetText = (await newTargetFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            changes.Add(new DocumentChange(targetFilePath, DocumentChangeKind.Modified, targetOldText, newTargetText));
        }

        return changes;
    }

    private static async Task<List<DocumentChange>> ApplyPullUpConstructorBodyAsync(
        MsSolution solution,
        PullUpConstructorBodyIntent intent,
        CancellationToken cancellationToken)
    {
        var subSymbol = await SymbolResolver.ResolveAsync(solution, intent.Subclass, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Subclass not found: {intent.Subclass}");
        var parentSymbol = await SymbolResolver.ResolveAsync(solution, intent.Parent, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Parent not found: {intent.Parent}");

        var subSyntaxRef = subSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.Subclass} has no source declaration.");
        var subDoc = solution.GetDocument(subSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.Subclass} not found.");
        var subFilePath = subDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.Subclass} has no filesystem path.");
        var subOldText = (await subDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var parentSyntaxRef = parentSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.Parent} has no source declaration.");
        var parentDoc = solution.GetDocument(parentSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.Parent} not found.");
        var parentFilePath = parentDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.Parent} has no filesystem path.");
        var parentOldText = (await parentDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var subRoot = await subDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Sub root unavailable.");
        var subClass = subRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == subSymbol.Name)
            ?? throw new InvalidOperationException($"Subclass node {subSymbol.Name} not found.");
        var subCtor = subClass.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Subclass {subSymbol.Name} has no constructor to pull up.");
        var subCtorBody = subCtor.Body
            ?? throw new InvalidOperationException($"Subclass constructor has no block body to pull up.");

        var pulledStatements = subCtorBody.Statements;
        if (pulledStatements.Count == 0)
        {
            throw new InvalidOperationException("Subclass constructor body is already empty.");
        }

        // Modify parent: add or extend its parameterless constructor with the pulled statements.
        var parentRoot = await parentDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Parent root unavailable.");
        var parentClass = parentRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == parentSymbol.Name)
            ?? throw new InvalidOperationException($"Parent class {parentSymbol.Name} not found.");
        var parentCtor = parentClass.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        ClassDeclarationSyntax newParentClass;
        if (parentCtor is null)
        {
            var newCtor = SyntaxFactory.ConstructorDeclaration(parentSymbol.Name)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList())
                .WithBody(SyntaxFactory.Block(pulledStatements))
                .WithAdditionalAnnotations(Formatter.Annotation);
            newParentClass = parentClass.AddMembers(newCtor)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }
        else
        {
            var oldBody = parentCtor.Body ?? SyntaxFactory.Block();
            var mergedBody = oldBody.WithStatements(oldBody.Statements.AddRange(pulledStatements));
            var newParentCtor = parentCtor.WithBody(mergedBody)
                .WithAdditionalAnnotations(Formatter.Annotation);
            newParentClass = parentClass.ReplaceNode(parentCtor, newParentCtor)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }
        var newParentRoot = parentRoot.ReplaceNode(parentClass, newParentClass);
        var newParentDoc = parentDoc.WithSyntaxRoot(newParentRoot);
        var parentFormatted = await Formatter.FormatAsync(newParentDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newParentText = (await parentFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        // Modify sub: replace ctor body with empty block, ensure `: base()` initializer.
        var emptyBody = SyntaxFactory.Block();
        var newSubCtor = subCtor.WithBody(emptyBody);
        if (newSubCtor.Initializer is null)
        {
            var baseInit = SyntaxFactory.ConstructorInitializer(
                SyntaxKind.BaseConstructorInitializer,
                SyntaxFactory.ArgumentList());
            newSubCtor = newSubCtor.WithInitializer(baseInit);
        }
        newSubCtor = newSubCtor.WithAdditionalAnnotations(Formatter.Annotation);
        var newSubClass = subClass.ReplaceNode(subCtor, newSubCtor)
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newSubRoot = subRoot.ReplaceNode(subClass, newSubClass);
        var newSubDoc = subDoc.WithSyntaxRoot(newSubRoot);
        var subFormatted = await Formatter.FormatAsync(newSubDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newSubText = (await subFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(parentOldText, newParentText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(parentFilePath, DocumentChangeKind.Modified, parentOldText, newParentText));
        }
        if (!string.Equals(subOldText, newSubText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(subFilePath, DocumentChangeKind.Modified, subOldText, newSubText));
        }
        return changes;
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyReplaceTypeCodeWithSubclassesAsync(
        MsSolution solution,
        ReplaceTypeCodeWithSubclassesIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        // Make owner abstract if not already.
        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        var newDoc = ownerDoc;
        var changes = new List<DocumentChange>();
        if (!ownerClass.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
        {
            var newModifiers = ownerClass.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
            var newOwnerClass = ownerClass.WithModifiers(newModifiers)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newRoot = root.ReplaceNode(ownerClass, newOwnerClass);
            newDoc = ownerDoc.WithSyntaxRoot(newRoot);
            var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            changes.Add(new DocumentChange(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText));
            newDoc = formatted;
        }

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (ownerSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : ownerSymbol.ContainingNamespace.ToDisplayString());
        var ownerName = ownerSymbol.Name;

        var project = newDoc.Project;
        foreach (var subName in intent.SubclassNames)
        {
            var subText = BuildSimpleSubclassSource(namespaceName, subName, ownerName);
            var subFilePath = Path.Combine(
                Path.GetDirectoryName(ownerFilePath)!,
                $"{subName}.cs");
            project = project.AddDocument(
                name: $"{subName}.cs",
                text: subText,
                folders: ownerDoc.Folders,
                filePath: subFilePath).Project;
            changes.Add(new DocumentChange(subFilePath, DocumentChangeKind.Added, OldText: null, NewText: subText));
        }

        return (project.Solution, changes);
    }

    private static string BuildSimpleSubclassSource(string namespaceName, string subclassName, string parentName)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("public class ").Append(subclassName).Append(" : ").AppendLine(parentName);
        sb.AppendLine("{");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Extract Hierarchy — Fowler Hard tier.
    // Same shell-subclass scaffold as ReplaceTypeCodeWithSubclasses, plus:
    //  * for every method in intent.MethodsToVirtualize, strip the body from
    //    the owner declaration and add `abstract` (owner will be forced
    //    abstract too, per the normal path);
    //  * every generated subclass gets an `override` stub for each virtualized
    //    method that throws NotImplementedException.
    // Migration of existing method bodies into the subclasses is left to the
    // user — the same principle #29 uses.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyExtractHierarchyAsync(
        MsSolution solution,
        ExtractHierarchyIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        // Collect the methods to virtualize by signature match; we need their
        // final identifier + parameter list for the override stubs, and we need
        // the syntax node handles to rewrite the owner class in one pass.
        var toVirtualize = new List<MethodDeclarationSyntax>();
        var virtualizedSignatures = new List<(string Name, string ParamList, string ReturnType)>();
        foreach (var wanted in intent.MethodsToVirtualize)
        {
            var wantedName = ExtractMemberNameFromSignature(wanted.Signature);
            var match = ownerClass.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == wantedName);
            if (match is null) continue;
            toVirtualize.Add(match);
            virtualizedSignatures.Add((
                match.Identifier.ValueText,
                match.ParameterList.ToString(),
                match.ReturnType.ToString()));
        }

        // Owner-class rewrite: add `abstract` on the class if missing, replace
        // each virtualized method with a body-less abstract declaration.
        var newOwnerClass = ownerClass;
        if (!newOwnerClass.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
        {
            newOwnerClass = newOwnerClass.WithModifiers(newOwnerClass.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword)));
        }

        foreach (var method in toVirtualize)
        {
            // Track by identity via ReplaceNode below; build the abstract
            // replacement now.
            var mods = method.Modifiers;
            if (!mods.Any(t => t.IsKind(SyntaxKind.AbstractKeyword)))
            {
                mods = mods.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
            }
            // Drop `virtual` / `override` — abstract subsumes them.
            mods = SyntaxFactory.TokenList(mods.Where(t =>
                !t.IsKind(SyntaxKind.VirtualKeyword) && !t.IsKind(SyntaxKind.OverrideKeyword)));

            var abstractMethod = method
                .WithModifiers(mods)
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithAdditionalAnnotations(Formatter.Annotation);
            newOwnerClass = newOwnerClass.ReplaceNode(
                newOwnerClass.Members.OfType<MethodDeclarationSyntax>()
                    .First(m => m.Identifier.ValueText == method.Identifier.ValueText
                                && m.ParameterList.ToString() == method.ParameterList.ToString()),
                abstractMethod);
        }
        newOwnerClass = newOwnerClass.WithAdditionalAnnotations(Formatter.Annotation);

        var changes = new List<DocumentChange>();
        var newRoot = root.ReplaceNode(ownerClass, newOwnerClass);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var ownerFormatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var ownerNewText = (await ownerFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        if (!string.Equals(ownerOldText, ownerNewText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, ownerNewText));
        }
        newDoc = ownerFormatted;

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (ownerSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : ownerSymbol.ContainingNamespace.ToDisplayString());
        var ownerName = ownerSymbol.Name;

        var project = newDoc.Project;
        foreach (var subName in intent.SubclassNames)
        {
            var subText = BuildSubclassSourceWithOverrides(namespaceName, subName, ownerName, virtualizedSignatures);
            var subFilePath = Path.Combine(
                Path.GetDirectoryName(ownerFilePath)!,
                $"{subName}.cs");
            project = project.AddDocument(
                name: $"{subName}.cs",
                text: subText,
                folders: ownerDoc.Folders,
                filePath: subFilePath).Project;
            changes.Add(new DocumentChange(subFilePath, DocumentChangeKind.Added, OldText: null, NewText: subText));
        }

        return (project.Solution, changes);
    }

    // Tease Apart Inheritance — Fowler Hard tier.
    // Scaffolds a NEW hierarchy (SecondaryHierarchyName + subclasses) alongside
    // the existing PrimaryHierarchyRoot, and adds a delegation field on the
    // primary so instances can hold + forward to a secondary. All existing
    // methods stay put; the user then Push-Down / Move-Method the ones that
    // varied along the second axis into the new subclasses. This mirrors #29's
    // scaffold-only philosophy — no code migration is inferred, only the shell
    // + the field are emitted.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyTeaseApartInheritanceAsync(
        MsSolution solution,
        TeaseApartInheritanceIntent intent,
        CancellationToken cancellationToken)
    {
        var primarySymbol = await SymbolResolver.ResolveAsync(solution, intent.PrimaryHierarchyRoot, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Primary hierarchy root not found: {intent.PrimaryHierarchyRoot}");
        var primarySyntaxRef = primarySymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.PrimaryHierarchyRoot} has no source declaration.");
        var primaryDoc = solution.GetDocument(primarySyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.PrimaryHierarchyRoot} not found.");
        var primaryFilePath = primaryDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.PrimaryHierarchyRoot} has no filesystem path.");
        var primaryOldText = (await primaryDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await primaryDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Primary root unavailable.");
        var primaryClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == primarySymbol.Name)
            ?? throw new InvalidOperationException($"Class {primarySymbol.Name} not found.");

        var changes = new List<DocumentChange>();

        // Only append the delegation field if it isn't already present — repeat
        // invocations of the intent shouldn't double-declare it.
        var fieldName = intent.DelegationFieldName;
        var fieldAlreadyPresent = primaryClass.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Any(v => v.Identifier.ValueText == fieldName);

        var newDoc = primaryDoc;
        if (!fieldAlreadyPresent)
        {
            var fieldDecl = SyntaxFactory.ParseMemberDeclaration(
                $"protected {intent.SecondaryHierarchyName}? {fieldName};")
                ?? throw new InvalidOperationException("Failed to synthesize delegation field.");
            fieldDecl = fieldDecl.WithAdditionalAnnotations(Formatter.Annotation);

            var newPrimary = primaryClass.WithMembers(primaryClass.Members.Insert(0, fieldDecl))
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newRoot = root.ReplaceNode(primaryClass, newPrimary);
            newDoc = primaryDoc.WithSyntaxRoot(newRoot);
            var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var primaryNewText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            if (!string.Equals(primaryOldText, primaryNewText, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(primaryFilePath, DocumentChangeKind.Modified, primaryOldText, primaryNewText));
            }
            newDoc = formatted;
        }

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (primarySymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : primarySymbol.ContainingNamespace.ToDisplayString());
        var project = newDoc.Project;
        var folder = Path.GetDirectoryName(primaryFilePath)!;

        // Abstract secondary root file.
        var secondaryFilePath = Path.Combine(folder, $"{intent.SecondaryHierarchyName}.cs");
        var secondaryText = BuildAbstractRootSource(namespaceName, intent.SecondaryHierarchyName);
        project = project.AddDocument(
            name: $"{intent.SecondaryHierarchyName}.cs",
            text: secondaryText,
            folders: primaryDoc.Folders,
            filePath: secondaryFilePath).Project;
        changes.Add(new DocumentChange(secondaryFilePath, DocumentChangeKind.Added, OldText: null, NewText: secondaryText));

        // One subclass file per secondary axis case.
        foreach (var subName in intent.SecondarySubclassNames)
        {
            var subText = BuildSimpleSubclassSource(namespaceName, subName, intent.SecondaryHierarchyName);
            var subFilePath = Path.Combine(folder, $"{subName}.cs");
            project = project.AddDocument(
                name: $"{subName}.cs",
                text: subText,
                folders: primaryDoc.Folders,
                filePath: subFilePath).Project;
            changes.Add(new DocumentChange(subFilePath, DocumentChangeKind.Added, OldText: null, NewText: subText));
        }

        return (project.Solution, changes);
    }

    // Convert Procedural Design to Objects — Fowler Hard tier.
    // For each method in MethodsToMove: verify the first parameter's type
    // matches DataRecordType, drop that parameter, rewrite references to it
    // in the body as `this`, remove `static`, then add the transformed method
    // to DataRecordType and delete the original from ProceduralClass. Call
    // sites `Proc.M(record, x)` are rewritten to `record.M(x)`.
    //
    // Methods whose first parameter doesn't match DataRecordType are silently
    // skipped — an intent listing 5 methods where 4 qualify and 1 doesn't
    // still succeeds for the 4.
    private async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyConvertProceduralToObjectsAsync(
        MsSolution solution,
        ConvertProceduralToObjectsIntent intent,
        CancellationToken cancellationToken)
    {
        var proceduralSymbol = await SymbolResolver.ResolveAsync(solution, intent.ProceduralClass, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Procedural class not found: {intent.ProceduralClass}");
        var dataRecordSymbol = await SymbolResolver.ResolveAsync(solution, intent.DataRecordType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Data record type not found: {intent.DataRecordType}");

        var proceduralSyntaxRef = proceduralSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ProceduralClass} has no source declaration.");
        var proceduralDoc = solution.GetDocument(proceduralSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.ProceduralClass} not found.");
        var proceduralFilePath = proceduralDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.ProceduralClass} has no filesystem path.");
        var proceduralOldText = (await proceduralDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var recordSyntaxRef = dataRecordSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.DataRecordType} has no source declaration.");
        var recordDoc = solution.GetDocument(recordSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.DataRecordType} not found.");
        var recordFilePath = recordDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.DataRecordType} has no filesystem path.");
        var recordOldText = (await recordDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        // The data record's short name is what a call-site parameter's Type
        // typically looks like. Handle both "MyLib.Data" and "Data" forms.
        var recordShortName = dataRecordSymbol.Name;

        var proceduralRoot = await proceduralDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("Procedural root unavailable.");
        var recordRoot = await recordDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Record root unavailable.");

        var proceduralClass = proceduralRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == proceduralSymbol.Name)
            ?? throw new InvalidOperationException($"Class {proceduralSymbol.Name} not found.");
        var recordClass = recordRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == dataRecordSymbol.Name)
            ?? throw new InvalidOperationException($"Type {dataRecordSymbol.Name} not found.");

        // Collect the methods to move + first-param-name pairs so we can build
        // both the removal list (procedural side) and the addition list
        // (record side) in one pass.
        var toRemove = new List<MethodDeclarationSyntax>();
        var toAddOnRecord = new List<MethodDeclarationSyntax>();
        var movedMethodNames = new List<string>();

        foreach (var wanted in intent.MethodsToMove)
        {
            var wantedName = ExtractMemberNameFromSignature(wanted.Signature);
            var method = proceduralClass.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == wantedName);
            if (method is null) continue;
            var parms = method.ParameterList.Parameters;
            if (parms.Count == 0) continue;
            var firstParam = parms[0];
            if (firstParam.Type is null) continue;
            // Compare by short name (handles both bare and qualified forms).
            var typeText = firstParam.Type.ToString();
            if (typeText != recordShortName
                && !typeText.EndsWith("." + recordShortName, StringComparison.Ordinal)
                && typeText != dataRecordSymbol.ToDisplayString()) continue;

            var firstParamName = firstParam.Identifier.ValueText;
            var newParams = SyntaxFactory.SeparatedList(parms.Skip(1));
            var newParamList = method.ParameterList.WithParameters(newParams);

            var rewriter = new SelfParameterRewriter(firstParamName);
            var newMethod = method.WithParameterList(newParamList);
            if (newMethod.Body is not null)
                newMethod = newMethod.WithBody((BlockSyntax)rewriter.Visit(newMethod.Body));
            if (newMethod.ExpressionBody is not null)
                newMethod = newMethod.WithExpressionBody(
                    (ArrowExpressionClauseSyntax)rewriter.Visit(newMethod.ExpressionBody));

            // Strip `static` — this becomes an instance method on the record.
            var modifiers = SyntaxFactory.TokenList(
                newMethod.Modifiers.Where(t => !t.IsKind(SyntaxKind.StaticKeyword)));
            newMethod = newMethod.WithModifiers(modifiers)
                .WithAdditionalAnnotations(Formatter.Annotation);

            toRemove.Add(method);
            toAddOnRecord.Add(newMethod);
            movedMethodNames.Add(wantedName);
        }

        var changes = new List<DocumentChange>();

        if (toRemove.Count == 0)
        {
            // Nothing to do — every requested method failed the first-param check.
            return (solution, changes);
        }

        // Rewrite the record document first (adds happen at end of members).
        var newRecordClass = recordClass.WithMembers(
            recordClass.Members.AddRange(toAddOnRecord))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newRecordRoot = recordRoot.ReplaceNode(recordClass, newRecordClass);
        var newRecordDoc = recordDoc.WithSyntaxRoot(newRecordRoot);
        var recordFormatted = await Formatter.FormatAsync(newRecordDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var recordNewText = (await recordFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        if (!string.Equals(recordOldText, recordNewText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(recordFilePath, DocumentChangeKind.Modified, recordOldText, recordNewText));
        }

        // Now rewrite the procedural document (remove the moved methods).
        // If procedural and record are the SAME file, apply the removals on
        // top of the record's updated syntax tree.
        Document proceduralWorkDoc;
        SyntaxNode proceduralWorkRoot;
        TypeDeclarationSyntax proceduralWorkClass;
        string proceduralWorkOldText;
        if (string.Equals(proceduralFilePath, recordFilePath, StringComparison.OrdinalIgnoreCase))
        {
            proceduralWorkDoc = recordFormatted;
            proceduralWorkRoot = await recordFormatted.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                                 ?? throw new InvalidOperationException("Root unavailable.");
            proceduralWorkClass = proceduralWorkRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.ValueText == proceduralSymbol.Name)
                ?? throw new InvalidOperationException($"Class {proceduralSymbol.Name} not found after record edit.");
            // Same-file case: record's change was already recorded above; the
            // subsequent procedural edit will REPLACE that entry, not add a
            // second one for the same path.
            proceduralWorkOldText = recordOldText;
            if (changes.Count > 0 && string.Equals(changes[^1].FilePath, proceduralFilePath, StringComparison.OrdinalIgnoreCase))
            {
                changes.RemoveAt(changes.Count - 1);
            }
        }
        else
        {
            proceduralWorkDoc = proceduralDoc;
            proceduralWorkRoot = proceduralRoot;
            proceduralWorkClass = proceduralClass;
            proceduralWorkOldText = proceduralOldText;
        }

        // Re-resolve the target nodes by identifier + parameter list in the
        // current tree (they may have been re-created after the record edit).
        var currentRemovals = new List<MethodDeclarationSyntax>();
        foreach (var original in toRemove)
        {
            var match = proceduralWorkClass.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == original.Identifier.ValueText
                                     && m.ParameterList.ToString() == original.ParameterList.ToString());
            if (match is not null) currentRemovals.Add(match);
        }
        var newProceduralClass = proceduralWorkClass.WithMembers(
            SyntaxFactory.List(proceduralWorkClass.Members.Where(m => !(m is MethodDeclarationSyntax mds && currentRemovals.Contains(mds)))))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newProceduralRoot = proceduralWorkRoot.ReplaceNode(proceduralWorkClass, newProceduralClass);
        var newProceduralDoc = proceduralWorkDoc.WithSyntaxRoot(newProceduralRoot);
        var proceduralFormatted = await Formatter.FormatAsync(newProceduralDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var proceduralNewText = (await proceduralFormatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        if (!string.Equals(proceduralWorkOldText, proceduralNewText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(proceduralFilePath, DocumentChangeKind.Modified, proceduralWorkOldText, proceduralNewText));
        }

        // Rewrite call sites: `Proc.M(record, args)` → `record.M(args)`.
        // Uses syntactic pattern matching (not SymbolFinder) because by this
        // point we've already REMOVED the method from ProceduralClass, so the
        // semantic definition is gone and SymbolFinder would return no refs.
        // Walking the AST for `ProceduralClassShortName.MethodName(...)`
        // invocations is enough — false positives (unrelated identifiers with
        // the same shape) are vanishingly rare in practice.
        var workingSolution = proceduralFormatted.Project.Solution;
        (workingSolution, var callSiteChanges) = await RewriteProceduralCallSitesSyntacticallyAsync(
            workingSolution,
            proceduralClassShortName: proceduralSymbol.Name,
            movedMethodNames: movedMethodNames,
            cancellationToken).ConfigureAwait(false);
        // A call-site rewrite may hit the same file we already recorded above
        // (same-file case: procedural + record + callers in one .cs). Merge
        // by dropping the earlier entry for that path — the newer NewText
        // was built on top of the earlier edits so it already includes them.
        foreach (var cs in callSiteChanges)
        {
            var existingIdx = changes.FindIndex(c => string.Equals(c.FilePath, cs.FilePath, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                changes[existingIdx] = new DocumentChange(cs.FilePath, DocumentChangeKind.Modified, changes[existingIdx].OldText, cs.NewText);
            }
            else
            {
                changes.Add(cs);
            }
        }

        return (workingSolution, changes);
    }

    private async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> RewriteProceduralCallSitesSyntacticallyAsync(
        MsSolution solution,
        string proceduralClassShortName,
        IReadOnlyList<string> movedMethodNames,
        CancellationToken cancellationToken)
    {
        var changes = new List<DocumentChange>();
        var wanted = new HashSet<string>(movedMethodNames, StringComparer.Ordinal);
        var currentSolution = solution;

        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null) continue;
                var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null) continue;

                var replacements = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();
                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax mae) continue;
                    var name = mae.Name.Identifier.ValueText;
                    if (!wanted.Contains(name)) continue;
                    // Match by short name suffix: `Proc.M` or `MyLib.Proc.M`.
                    var receiverText = mae.Expression.ToString();
                    if (receiverText != proceduralClassShortName
                        && !receiverText.EndsWith("." + proceduralClassShortName, StringComparison.Ordinal))
                        continue;
                    if (inv.ArgumentList.Arguments.Count == 0) continue;

                    var firstArgExpr = inv.ArgumentList.Arguments[0].Expression;
                    var newReceiver = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        firstArgExpr.WithoutTrivia(),
                        SyntaxFactory.IdentifierName(name));
                    var newArgs = inv.ArgumentList.WithArguments(
                        SyntaxFactory.SeparatedList(inv.ArgumentList.Arguments.Skip(1)));
                    var newInv = inv
                        .WithExpression(newReceiver)
                        .WithArgumentList(newArgs)
                        .WithAdditionalAnnotations(Formatter.Annotation);
                    replacements[inv] = newInv;
                }
                if (replacements.Count == 0) continue;

                var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
                var newDoc = doc.WithSyntaxRoot(newRoot);
                var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
                var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                {
                    changes.Add(new DocumentChange(doc.FilePath, DocumentChangeKind.Modified, oldText, newText));
                    currentSolution = formatted.Project.Solution;
                }
            }
        }
        return (currentSolution, changes);
    }


    // Rewrites the identifier `_paramName` (bare or as the LHS of a member
    // access) to `this`. Used when moving a static method onto the type of
    // its first parameter — inside the moved body, that first-param
    // identifier should now be the receiver.
    private sealed class SelfParameterRewriter : CSharpSyntaxRewriter
    {
        private readonly string _paramName;
        public SelfParameterRewriter(string paramName) { _paramName = paramName; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.ValueText != _paramName) return base.VisitIdentifierName(node);
            // Skip cases where the identifier is on the RHS of a member access
            // (`x.foo` → we only want to touch `x`, not `foo`) — check parent.
            if (node.Parent is MemberAccessExpressionSyntax mae && ReferenceEquals(mae.Name, node))
                return base.VisitIdentifierName(node);
            return SyntaxFactory.ThisExpression()
                .WithTriviaFrom(node);
        }
    }

    // Extract Method — Fowler Composing Methods.
    // MVP scope: statement-list extraction; params inferred from DataFlowsIn
    // (locals / parameters read inside but assigned outside); return value
    // inferred from DataFlowsOut (exactly one → return, zero → void); throws
    // NotSupportedException for cases the MVP doesn't handle (multi-return,
    // ref/out params, control-flow escape via return/break/continue/goto,
    // or a selection that isn't a full statement list).
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyExtractMethodAsync(
        MsSolution solution,
        ExtractMethodIntent intent,
        CancellationToken cancellationToken)
    {
        var typeSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");

        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var semantic = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Semantic model unavailable.");

        var containingMethodNode = (await syntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false))
                                   as MethodDeclarationSyntax
                                   ?? throw new InvalidOperationException("Containing method's syntax is not a MethodDeclaration.");
        if (containingMethodNode.Body is null)
            throw new NotSupportedException("Extract Method requires the containing method to have a block body (expression-bodied methods aren't supported yet).");

        var selectionSpan = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);

        // Collect statements FULLY contained in the selection, directly under
        // the containing method's block. Statements nested inside blocks that
        // aren't fully selected are refused (partial-block extraction is
        // outside the MVP).
        var selectedStatements = containingMethodNode.Body.Statements
            .Where(s => selectionSpan.Contains(s.Span))
            .ToList();
        if (selectedStatements.Count == 0)
            throw new InvalidOperationException("Selection does not cover any complete statement in the containing method's body. Adjust the selection to include whole statements.");

        // Refuse control-flow-escaping selections — extraction would change
        // semantics unless we generate try/finally or continuation shim.
        foreach (var stmt in selectedStatements)
        {
            var escape = stmt.DescendantNodesAndSelf().FirstOrDefault(n =>
                n is ReturnStatementSyntax
                || n is BreakStatementSyntax
                || n is ContinueStatementSyntax
                || n is GotoStatementSyntax
                || n is YieldStatementSyntax);
            if (escape is not null)
                throw new NotSupportedException($"Selection contains {escape.Kind()} which would escape the extracted method. MVP Extract Method doesn't handle control-flow-escaping selections yet.");
        }

        // Data flow: figure out what parameters the new method needs and
        // whether it needs a return value.
        var dataFlow = semantic.AnalyzeDataFlow(selectedStatements[0], selectedStatements[^1])
            ?? throw new InvalidOperationException("Data flow analysis failed on the selected statements.");
        if (!dataFlow.Succeeded)
            throw new InvalidOperationException("Data flow analysis reported failure — the selection isn't cleanly extractable.");

        static bool IsParamOrLocal(ISymbol s) => s is ILocalSymbol or IParameterSymbol;

        var inFlow = dataFlow.DataFlowsIn.Where(IsParamOrLocal).Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();
        var outFlow = dataFlow.DataFlowsOut.Where(IsParamOrLocal).Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();

        if (outFlow.Count > 1)
            throw new NotSupportedException($"Selection has {outFlow.Count} variables that flow out of the selection ({string.Join(", ", outFlow.Select(s => s.Name))}). MVP Extract Method supports at most one return value.");

        ISymbol? returnSymbol = outFlow.Count == 1 ? outFlow[0] : null;

        // Build parameter list from inFlow (skip anything that's also the
        // returnSymbol — that becomes both input and output, which we'd
        // usually model as `ref`; MVP just leaves it as a param and returns
        // its final value).
        var newParams = SyntaxFactory.SeparatedList(inFlow.Select(sym =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(sym.Name))
                .WithType(SyntaxFactory.ParseTypeName(TypeToDisplayString(sym)))));
        var newParamList = SyntaxFactory.ParameterList(newParams);

        // Return type
        TypeSyntax returnTypeSyntax = returnSymbol is null
            ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
            : SyntaxFactory.ParseTypeName(TypeToDisplayString(returnSymbol));

        // Build the extracted method body: the selected statements, plus a
        // trailing `return {name};` when we have a return symbol.
        var bodyStatements = new List<StatementSyntax>(selectedStatements);
        if (returnSymbol is not null)
        {
            bodyStatements.Add(SyntaxFactory.ReturnStatement(
                SyntaxFactory.IdentifierName(returnSymbol.Name)));
        }
        var newMethodBody = SyntaxFactory.Block(bodyStatements);

        var newMethodModifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (containingSymbol.IsStatic)
            newMethodModifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        var newMethod = SyntaxFactory.MethodDeclaration(returnTypeSyntax, intent.NewMethodName)
            .WithModifiers(SyntaxFactory.TokenList(newMethodModifiers))
            .WithParameterList(newParamList)
            .WithBody(newMethodBody)
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Build the invocation statement to replace the selection.
        var callArgs = SyntaxFactory.SeparatedList(inFlow.Select(sym =>
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(sym.Name))));
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(intent.NewMethodName),
            SyntaxFactory.ArgumentList(callArgs));

        StatementSyntax replacementStatement;
        if (returnSymbol is null)
        {
            replacementStatement = SyntaxFactory.ExpressionStatement(invocation);
        }
        else
        {
            // If the returnSymbol is a local declared inside the selection,
            // callers should receive it via `var x = NewMethod(...)`. If it's
            // an outer local/parameter, we assign to the existing name.
            var declaredInside = dataFlow.VariablesDeclared.Any(v =>
                SymbolEqualityComparer.Default.Equals(v, returnSymbol));
            if (declaredInside)
            {
                replacementStatement = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(returnSymbol.Name)
                                .WithInitializer(SyntaxFactory.EqualsValueClause(invocation)))));
            }
            else
            {
                replacementStatement = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(returnSymbol.Name),
                        invocation));
            }
        }
        replacementStatement = replacementStatement.WithAdditionalAnnotations(Formatter.Annotation);

        // Rewrite: replace the run of selected statements with the single
        // invocation, and insert newMethod right after containingMethod.
        var containingClass = containingMethodNode.FirstAncestorOrSelf<TypeDeclarationSyntax>()
            ?? throw new InvalidOperationException("Containing method has no enclosing type declaration.");

        var editedBodyStatements = containingMethodNode.Body.Statements;
        var firstIdx = editedBodyStatements.IndexOf(selectedStatements[0]);
        var reducedStatements = editedBodyStatements.ToList();
        reducedStatements.RemoveRange(firstIdx, selectedStatements.Count);
        reducedStatements.Insert(firstIdx, replacementStatement);
        var newContainingBody = containingMethodNode.Body.WithStatements(SyntaxFactory.List(reducedStatements));
        var newContainingMethod = containingMethodNode.WithBody(newContainingBody)
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Two-node replacement: swap containingMethod for [containingMethod', newMethod]
        // inside the class's Members list.
        var classMembers = containingClass.Members;
        var methodIdx = classMembers.IndexOf(containingMethodNode);
        var newClassMembers = classMembers
            .Replace(containingMethodNode, newContainingMethod)
            .Insert(methodIdx + 1, newMethod);
        var newContainingClass = containingClass.WithMembers(newClassMembers)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(containingClass, newContainingClass);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        }
        return (formatted.Project.Solution, changes);
    }

    // Extract Variable — Fowler Composing Methods.
    // Selection must be a valid expression inside the containing member's
    // body. A `var {NewName} = {expr};` local is inserted before the
    // innermost enclosing statement, and the selected expression is
    // replaced with an IdentifierName reference.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyExtractVariableAsync(
        MsSolution solution,
        ExtractVariableIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");

        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");

        var selectionSpan = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);

        // Find the innermost expression whose span is exactly the selection
        // (or the smallest expression containing the selection).
        var node = root.FindNode(selectionSpan, getInnermostNodeForTie: true);
        var targetExpr = node.AncestorsAndSelf().OfType<ExpressionSyntax>()
            .FirstOrDefault(e => selectionSpan.Contains(e.Span) || e.Span.Contains(selectionSpan))
            ?? throw new InvalidOperationException("Selection does not cover a valid expression. Adjust to select a complete expression.");
        // Prefer the tightest node whose span matches the selection.
        if (!selectionSpan.Contains(targetExpr.Span))
        {
            // targetExpr wraps the selection — try to descend to the exact-span child.
            var tighter = targetExpr.DescendantNodes()
                .OfType<ExpressionSyntax>()
                .FirstOrDefault(e => e.Span.Start == selectionSpan.Start && e.Span.End == selectionSpan.End);
            if (tighter is not null) targetExpr = tighter;
        }

        // Find the containing statement whose statement-list we can grow.
        var containingStatement = targetExpr.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException("Expression has no enclosing statement.");
        // The statement must live directly under a Block (method body /
        // if-block / etc). If it doesn't, walk up until we find one.
        while (containingStatement.Parent is not BlockSyntax)
        {
            var next = containingStatement.Parent as StatementSyntax;
            if (next is null)
                throw new NotSupportedException("Enclosing statement isn't inside a block — insert-before target unresolved. Reformat the containing expression so it lives inside a { } block.");
            containingStatement = next;
        }
        var parentBlock = (BlockSyntax)containingStatement.Parent!;

        // Build `var {NewName} = {expr};`.
        var newLocal = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(intent.NewVariableName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            targetExpr.WithoutTrivia())))))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newReference = SyntaxFactory.IdentifierName(intent.NewVariableName)
            .WithTriviaFrom(targetExpr);

        // Two edits happen within the same block:
        //   1) targetExpr → newReference
        //   2) newLocal inserted before containingStatement
        // Do them by rebuilding the block once — first swap expr → identifier
        // on the block subtree (which produces a new containing statement),
        // then insert newLocal before that new statement.
        var stmtIdxOriginal = parentBlock.Statements.IndexOf(containingStatement);
        var newContainingStatement = (StatementSyntax)containingStatement.ReplaceNode(targetExpr, newReference);
        var newBlockStatements = parentBlock.Statements
            .Replace(containingStatement, newContainingStatement)
            .Insert(stmtIdxOriginal, newLocal);
        var updatedBlock = parentBlock.WithStatements(newBlockStatements)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(parentBlock, updatedBlock);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        }
        return (formatted.Project.Solution, changes);
    }

    // Inline Method — Fowler Composing Methods.
    // MVP scope: the target method must be expression-bodied OR a block body
    // with exactly one `return expr;`. Substitutes params in the extracted
    // expression with the call site's arguments and replaces the call.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyInlineMethodAsync(
        MsSolution solution,
        InlineMethodIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var semantic = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Semantic model unavailable.");

        var span = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException("Selection does not cover a method invocation. Click on (or select) the call site to inline.");

        // Resolve the target method's declaration.
        if (semantic.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol targetMethod)
            throw new InvalidOperationException("Could not resolve the method at the selected call site.");
        var targetSyntaxRef = targetMethod.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new NotSupportedException("Target method has no source declaration (declared in metadata or generated). MVP requires source.");
        var targetNode = await targetSyntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) as MethodDeclarationSyntax
            ?? throw new NotSupportedException("Target method's syntax isn't a MethodDeclaration.");

        // Extract the single expression that represents the method's value.
        ExpressionSyntax? bodyExpr = null;
        if (targetNode.ExpressionBody is { } arrow)
        {
            bodyExpr = arrow.Expression;
        }
        else if (targetNode.Body is { } block && block.Statements.Count == 1
                 && block.Statements[0] is ReturnStatementSyntax ret && ret.Expression is not null)
        {
            bodyExpr = ret.Expression;
        }
        if (bodyExpr is null)
            throw new NotSupportedException("MVP Inline Method requires the target to be expression-bodied or a block body with a single `return expr;`.");

        // Build the parameter → argument substitution map.
        var argExprs = invocation.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        var parms = targetNode.ParameterList.Parameters;
        if (argExprs.Count != parms.Count)
            throw new NotSupportedException($"Argument count ({argExprs.Count}) doesn't match parameter count ({parms.Count}). MVP Inline Method doesn't handle default parameters yet.");
        var paramMap = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        for (int i = 0; i < parms.Count; i++)
        {
            // Wrap each arg in parens so operator precedence at the point of
            // substitution stays correct — the arg might be `x + 1` and the
            // body might be `n * 2`, and we don't want `x + 1 * 2`.
            var argExpr = argExprs[i];
            var wrapped = argExpr is LiteralExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ParenthesizedExpressionSyntax
                ? argExpr
                : SyntaxFactory.ParenthesizedExpression(argExpr);
            paramMap[parms[i].Identifier.ValueText] = wrapped;
        }

        var substituted = (ExpressionSyntax)new IdentifierSubstituteRewriter(paramMap).Visit(bodyExpr);
        // Wrap in parentheses so operator precedence at the call site stays correct.
        var parenthesized = SyntaxFactory.ParenthesizedExpression(substituted)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(invocation, parenthesized);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    private sealed class IdentifierSubstituteRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, ExpressionSyntax> _map;
        public IdentifierSubstituteRewriter(Dictionary<string, ExpressionSyntax> map) { _map = map; }
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (_map.TryGetValue(node.Identifier.ValueText, out var replacement))
            {
                // Don't rewrite the RHS of a member access (`x.foo`) — we
                // only substitute for standalone identifiers that reference
                // the parameter, not any member with the same name.
                if (node.Parent is MemberAccessExpressionSyntax mae && ReferenceEquals(mae.Name, node))
                    return base.VisitIdentifierName(node);
                return replacement.WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }
    }

    // Inline Variable — Fowler Composing Methods.
    // MVP scope: local must have an initializer AND never be reassigned
    // inside the containing method. All references to the local are
    // replaced with the initializer expression; the declaration is removed.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyInlineVariableAsync(
        MsSolution solution,
        InlineVariableIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var semantic = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Semantic model unavailable.");

        var containingMethodNode = await syntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) as MethodDeclarationSyntax
            ?? throw new InvalidOperationException("Containing method's syntax is not a MethodDeclaration.");
        if (containingMethodNode.Body is null)
            throw new NotSupportedException("Inline Variable requires the containing method to have a block body.");

        var span = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);
        var node = root.FindNode(span, getInnermostNodeForTie: true);

        // Selection may point at either the VariableDeclaratorSyntax
        // (declaration) or an IdentifierNameSyntax (a use). Resolve to the
        // local symbol either way.
        ILocalSymbol? localSymbol = null;
        VariableDeclaratorSyntax? declarator = null;
        if (node.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } decl)
        {
            declarator = decl;
            localSymbol = semantic.GetDeclaredSymbol(decl, cancellationToken) as ILocalSymbol;
        }
        else if (node.AncestorsAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault() is { } id)
        {
            localSymbol = semantic.GetSymbolInfo(id, cancellationToken).Symbol as ILocalSymbol;
        }
        if (localSymbol is null)
            throw new InvalidOperationException("Selection does not resolve to a local variable. Click the declaration or any use of the local.");

        // Find the declarator if we only had a use.
        if (declarator is null)
        {
            var localSyntaxRef = localSymbol.DeclaringSyntaxReferences.FirstOrDefault()
                ?? throw new NotSupportedException("Local has no declaring syntax reference.");
            declarator = await localSyntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) as VariableDeclaratorSyntax
                ?? throw new NotSupportedException("Local's declaration syntax isn't a VariableDeclarator.");
        }
        if (declarator.Initializer?.Value is not { } initExpr)
            throw new NotSupportedException("Local has no initializer — nothing to inline.");

        // Refuse if the local is reassigned anywhere in the containing method.
        var declStatement = declarator.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>()
            ?? throw new NotSupportedException("Declarator isn't inside a LocalDeclarationStatement.");
        var localName = localSymbol.Name;
        var reassignments = containingMethodNode.Body.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax ins && ins.Identifier.ValueText == localName)
            .ToList();
        if (reassignments.Count > 0)
            throw new NotSupportedException("Local is reassigned after its declaration — inlining would change semantics. MVP requires single-assignment locals.");

        // Collect all use sites (excluding the declarator's own identifier).
        var uses = containingMethodNode.Body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == localName
                         && semantic.GetSymbolInfo(id, cancellationToken).Symbol is ILocalSymbol ls
                         && SymbolEqualityComparer.Default.Equals(ls, localSymbol))
            .ToList();

        // Rewrite: swap each use with the initializer (parenthesized for safety),
        // then remove the LocalDeclarationStatement from the containing block.
        var replacementExpr = SyntaxFactory.ParenthesizedExpression(initExpr.WithoutTrivia());
        var nodesToReplace = new List<SyntaxNode>(uses) { declStatement };
        var newRoot = root.ReplaceNodes(nodesToReplace, (orig, _) =>
        {
            if (ReferenceEquals(orig, declStatement))
                return null!;  // Marked for removal via SyntaxRemover below.
            return replacementExpr.WithTriviaFrom(orig).WithAdditionalAnnotations(Formatter.Annotation);
        });
        // ReplaceNodes doesn't remove — we need SyntaxRemoveOptions. Re-fetch
        // the declStatement in the rewritten tree and Remove it.
        var newDeclStatement = newRoot.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(l => l.Declaration.Variables.Any(v => v.Identifier.ValueText == localName
                && v.SpanStart == declarator.SpanStart));
        if (newDeclStatement is not null)
        {
            newRoot = newRoot.RemoveNode(newDeclStatement, SyntaxRemoveOptions.KeepNoTrivia)!;
        }

        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    // Decompose Conditional — Fowler Simplifying Conditional Expressions.
    // Selection points at an IfStatementSyntax. Extracts the condition into a
    // bool-returning method and each branch into a void method, then rewrites
    // the if to call those methods. Parameters for each extracted method are
    // inferred via DataFlowAnalysis (locals + params flowing in from outer
    // scope). Refuses if either branch has variables flowing out (MVP scope).
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyDecomposeConditionalAsync(
        MsSolution solution,
        DecomposeConditionalIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var semantic = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Semantic model unavailable.");

        var span = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var ifStmt = node.AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException("Selection does not cover an if statement.");

        var containingMethodNode = (await syntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false))
                                   as MethodDeclarationSyntax
                                   ?? throw new InvalidOperationException("Containing method's syntax is not a MethodDeclaration.");
        var containingClass = containingMethodNode.FirstAncestorOrSelf<TypeDeclarationSyntax>()
            ?? throw new InvalidOperationException("Containing method has no enclosing type declaration.");

        static bool IsParamOrLocal(ISymbol s) => s is ILocalSymbol or IParameterSymbol;

        // --- Extract the condition (bool method) ---
        var condExprAnalysis = semantic.AnalyzeDataFlow(ifStmt.Condition)
            ?? throw new InvalidOperationException("Data flow analysis failed for the condition.");
        var condParams = condExprAnalysis.DataFlowsIn.Where(IsParamOrLocal).Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();
        var conditionMethod = BuildParameterlessOrParameterizedMethod(
            name: intent.ConditionMethodName,
            returnType: SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
            parms: condParams,
            body: null,
            arrowBody: ifStmt.Condition.WithoutTrivia(),
            isStatic: containingSymbol.IsStatic);

        // --- Extract the then-block ---
        var (thenStatements, thenParams) = ExtractBranchStatementsAndParams(semantic, ifStmt.Statement, "then");
        var thenMethod = BuildParameterlessOrParameterizedMethod(
            name: intent.ThenMethodName,
            returnType: SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
            parms: thenParams,
            body: SyntaxFactory.Block(thenStatements),
            arrowBody: null,
            isStatic: containingSymbol.IsStatic);

        // --- Extract the else-block (if present) ---
        MethodDeclarationSyntax? elseMethod = null;
        List<ISymbol>? elseParams = null;
        if (ifStmt.Else is { } elseClause)
        {
            var (elseStatements, ep) = ExtractBranchStatementsAndParams(semantic, elseClause.Statement, "else");
            elseParams = ep;
            var name = string.IsNullOrWhiteSpace(intent.ElseMethodName)
                ? intent.ThenMethodName + "Else"
                : intent.ElseMethodName!;
            elseMethod = BuildParameterlessOrParameterizedMethod(
                name: name,
                returnType: SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                parms: elseParams,
                body: SyntaxFactory.Block(elseStatements),
                arrowBody: null,
                isStatic: containingSymbol.IsStatic);
        }

        // --- Build the new if statement calling into the extracted methods ---
        static InvocationExpressionSyntax BuildCall(string name, IReadOnlyList<ISymbol> parms)
            => SyntaxFactory.InvocationExpression(
                SyntaxFactory.IdentifierName(name),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    parms.Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Name))))));

        var newConditionCall = BuildCall(intent.ConditionMethodName, condParams);
        var newThenBlock = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
            BuildCall(intent.ThenMethodName, thenParams)));
        ElseClauseSyntax? newElseClause = null;
        if (elseMethod is not null && elseParams is not null)
        {
            var elseName = string.IsNullOrWhiteSpace(intent.ElseMethodName)
                ? intent.ThenMethodName + "Else"
                : intent.ElseMethodName!;
            newElseClause = SyntaxFactory.ElseClause(
                SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    BuildCall(elseName, elseParams))));
        }

        var newIf = SyntaxFactory.IfStatement(newConditionCall, newThenBlock, newElseClause)
            .WithTriviaFrom(ifStmt)
            .WithAdditionalAnnotations(Formatter.Annotation);

        // --- Assemble the modified containing class: swap if, insert new methods after containing method ---
        var newContainingMethod = containingMethodNode.ReplaceNode(ifStmt, newIf)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newMembers = containingClass.Members.Replace(containingMethodNode, newContainingMethod);
        var idxAfterMethod = newMembers.IndexOf(newContainingMethod) + 1;
        newMembers = newMembers.Insert(idxAfterMethod, conditionMethod);
        newMembers = newMembers.Insert(idxAfterMethod + 1, thenMethod);
        if (elseMethod is not null)
            newMembers = newMembers.Insert(idxAfterMethod + 2, elseMethod);

        var newContainingClass = containingClass.WithMembers(newMembers)
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newRoot = root.ReplaceNode(containingClass, newContainingClass);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    // Consolidate Conditional Expression — Fowler Simplifying Conditionals.
    // Selection covers 2+ consecutive if-statements at the same block level.
    // Merges them into one if whose condition is `cond1 || cond2 || ...`
    // and whose body is the first if's body. Refuses if any if has an else
    // clause or if bodies aren't syntactically identical (normalized text).
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyConsolidateConditionalExpressionAsync(
        MsSolution solution,
        ConsolidateConditionalExpressionIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");

        var span = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);

        var containingMethodNode = (await syntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false))
                                   as MethodDeclarationSyntax
                                   ?? throw new InvalidOperationException("Containing method's syntax is not a MethodDeclaration.");
        if (containingMethodNode.Body is null)
            throw new NotSupportedException("Consolidate requires a block-bodied containing method.");

        // Locate the block that owns the run of ifs (the innermost block
        // whose statements fully cover our selection).
        var containingBlock = containingMethodNode.Body.DescendantNodesAndSelf()
            .OfType<BlockSyntax>()
            .Where(b => b.Span.Contains(span))
            .OrderByDescending(b => b.SpanStart)
            .FirstOrDefault()
            ?? containingMethodNode.Body;

        var ifs = containingBlock.Statements
            .OfType<IfStatementSyntax>()
            .Where(s => span.OverlapsWith(s.Span))
            .ToList();
        if (ifs.Count < 2)
            throw new InvalidOperationException("Selection must cover at least two consecutive if-statements to consolidate.");

        // Verify they're CONSECUTIVE (no other statement between them).
        var firstIdx = containingBlock.Statements.IndexOf(ifs[0]);
        for (int i = 0; i < ifs.Count; i++)
        {
            var expected = containingBlock.Statements[firstIdx + i];
            if (!ReferenceEquals(expected, ifs[i]))
                throw new InvalidOperationException("Selection covers if-statements that aren't consecutive in the block — consolidate requires an unbroken run.");
        }

        // Verify none have an else clause.
        foreach (var i in ifs)
        {
            if (i.Else is not null)
                throw new NotSupportedException("Consolidate MVP refuses if-statements with else clauses — remove the elses or consolidate a smaller run.");
        }

        // Verify bodies are identical (normalized text).
        static string Norm(SyntaxNode n) => string.Join(" ", n.ToFullString().Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries));
        var firstBodyNorm = Norm(ifs[0].Statement);
        for (int i = 1; i < ifs.Count; i++)
        {
            if (Norm(ifs[i].Statement) != firstBodyNorm)
                throw new NotSupportedException($"Consolidate requires all selected if bodies to be syntactically identical — if #{i + 1}'s body differs.");
        }

        // Build the merged condition: cond1 || cond2 || ... . Each condition
        // is wrapped in parens so operator precedence inside a condition
        // stays right (`a && b` becomes `(a && b) || ...`).
        static ExpressionSyntax Wrap(ExpressionSyntax e) =>
            e is ParenthesizedExpressionSyntax or IdentifierNameSyntax or LiteralExpressionSyntax
                or InvocationExpressionSyntax or MemberAccessExpressionSyntax
                ? e
                : SyntaxFactory.ParenthesizedExpression(e);

        ExpressionSyntax merged = Wrap(ifs[0].Condition.WithoutTrivia());
        for (int i = 1; i < ifs.Count; i++)
        {
            merged = SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalOrExpression,
                merged,
                Wrap(ifs[i].Condition.WithoutTrivia()));
        }

        var mergedIf = SyntaxFactory.IfStatement(merged, ifs[0].Statement)
            .WithTriviaFrom(ifs[0])
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Swap the block: replace the whole run with the single merged if.
        var newBlockStatements = containingBlock.Statements.ToList();
        newBlockStatements.RemoveRange(firstIdx, ifs.Count);
        newBlockStatements.Insert(firstIdx, mergedIf);
        var newBlock = containingBlock.WithStatements(SyntaxFactory.List(newBlockStatements))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(containingBlock, newBlock);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    // Consolidate Duplicate Conditional Fragments — Fowler Simplifying Conditionals.
    // Selection points at an IfStatementSyntax with an else clause. Detects
    // a common PREFIX (statements identical at the start of both branches)
    // and a common SUFFIX (identical at the end); hoists the prefix before
    // the if and the suffix after. Refuses if the if has no else, or if
    // neither prefix nor suffix has any duplicate.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyConsolidateDuplicateConditionalFragmentsAsync(
        MsSolution solution,
        ConsolidateDuplicateConditionalFragmentsIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var span = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var ifStmt = node.AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException("Selection does not cover an if-statement.");
        if (ifStmt.Else is null)
            throw new NotSupportedException("Consolidate Duplicate Conditional Fragments requires the if to have an else clause.");

        // Both branches must be blocks — a bare statement branch has no
        // list to hoist from.
        if (ifStmt.Statement is not BlockSyntax thenBlock)
            throw new NotSupportedException("Then branch must be a block ({ ... }) for consolidation.");
        if (ifStmt.Else.Statement is not BlockSyntax elseBlock)
            throw new NotSupportedException("Else branch must be a block ({ ... }) for consolidation.");
        if (ifStmt.Parent is not BlockSyntax parentBlock)
            throw new NotSupportedException("Enclosing scope must be a block — the hoisted fragments need a place to live.");

        var thenStmts = thenBlock.Statements;
        var elseStmts = elseBlock.Statements;

        static string Norm(SyntaxNode n) => string.Join(" ", n.ToFullString().Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries));

        // Common prefix length.
        int prefix = 0;
        var maxPrefix = System.Math.Min(thenStmts.Count, elseStmts.Count);
        while (prefix < maxPrefix && Norm(thenStmts[prefix]) == Norm(elseStmts[prefix]))
            prefix++;

        // Common suffix length — don't overlap the prefix on either side.
        int suffix = 0;
        var maxSuffix = System.Math.Min(thenStmts.Count - prefix, elseStmts.Count - prefix);
        while (suffix < maxSuffix
               && Norm(thenStmts[thenStmts.Count - 1 - suffix]) == Norm(elseStmts[elseStmts.Count - 1 - suffix]))
            suffix++;

        if (prefix == 0 && suffix == 0)
            throw new InvalidOperationException("No duplicate statements at the top or bottom of the branches — nothing to consolidate.");

        // Statements to keep inside each branch.
        var newThen = thenStmts.Skip(prefix).Take(thenStmts.Count - prefix - suffix).ToList();
        var newElse = elseStmts.Skip(prefix).Take(elseStmts.Count - prefix - suffix).ToList();

        var newThenBlock = thenBlock.WithStatements(SyntaxFactory.List(newThen));
        var newElseBlock = elseBlock.WithStatements(SyntaxFactory.List(newElse));
        var newIfStmt = ifStmt
            .WithStatement(newThenBlock)
            .WithElse(ifStmt.Else.WithStatement(newElseBlock))
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Statements to hoist — take from THEN so their trivia stays sensible.
        var prefixStmts = thenStmts.Take(prefix).Select(s => s.WithoutTrivia()).ToList();
        var suffixStmts = thenStmts.Skip(thenStmts.Count - suffix).Select(s => s.WithoutTrivia()).ToList();

        // Rebuild the enclosing block: hoist prefix + new if + hoist suffix.
        var newEnclosing = parentBlock.Statements.ToList();
        var ifIdx = newEnclosing.IndexOf(ifStmt);
        newEnclosing.RemoveAt(ifIdx);
        var insertAt = ifIdx;
        foreach (var s in prefixStmts)
            newEnclosing.Insert(insertAt++, s);
        newEnclosing.Insert(insertAt++, newIfStmt);
        foreach (var s in suffixStmts)
            newEnclosing.Insert(insertAt++, s);
        var updatedParent = parentBlock.WithStatements(SyntaxFactory.List(newEnclosing))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(parentBlock, updatedParent);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    // Introduce Assertion — Fowler Simplifying Conditionals.
    // Inserts `System.Diagnostics.Debug.Assert(condition, "message");` at
    // the TOP of the smallest block that contains the caret position. The
    // typical use — assert a precondition at method entry — falls out of
    // planting the caret anywhere inside the method body.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyIntroduceAssertionAsync(
        MsSolution solution,
        IntroduceAssertionIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var containingMethodNode = (await syntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false))
                                   as MethodDeclarationSyntax
                                   ?? throw new InvalidOperationException("Containing method's syntax is not a MethodDeclaration.");
        if (containingMethodNode.Body is null)
            throw new NotSupportedException("Introduce Assertion requires a block-bodied containing method.");

        // Find the block enclosing the caret (may be the method body itself,
        // or a nested block for scoped assertions).
        var caret = intent.SelectionStart;
        var targetBlock = containingMethodNode.Body.DescendantNodesAndSelf()
            .OfType<BlockSyntax>()
            .Where(b => b.Span.Contains(caret) || (b == containingMethodNode.Body && b.Span.End >= caret))
            .OrderByDescending(b => b.SpanStart)
            .FirstOrDefault()
            ?? containingMethodNode.Body;

        // Build the assertion statement.
        var msg = intent.Message ?? intent.AssertionExpression;
        var assertText = $"System.Diagnostics.Debug.Assert({intent.AssertionExpression}, {SyntaxFactory.Literal(msg)});";
        var assertStmt = SyntaxFactory.ParseStatement(assertText)
                            .WithAdditionalAnnotations(Formatter.Annotation);

        var newBlockStatements = targetBlock.Statements.Insert(0, assertStmt);
        var newBlock = targetBlock.WithStatements(newBlockStatements)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(targetBlock, newBlock);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    // Introduce Null Object — Fowler Simplifying Conditionals.
    // Scaffolds a `Null{SourceType}` subclass file with override stubs for
    // every virtual/abstract instance method on the source type. Void
    // overrides are empty; value-returning overrides return `default`;
    // abstract methods force the compiler to keep the override, and users
    // fill in the sensible no-op value manually.
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyIntroduceNullObjectAsync(
        MsSolution solution,
        IntroduceNullObjectIntent intent,
        CancellationToken cancellationToken)
    {
        var typeSymbol = await SymbolResolver.ResolveAsync(solution, intent.SourceType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Source type not found: {intent.SourceType}");
        if (typeSymbol.IsSealed && !typeSymbol.IsAbstract)
            throw new NotSupportedException($"{intent.SourceType} is sealed — cannot introduce a Null Object subclass. Consider dropping `sealed` first.");

        var srcSyntaxRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.SourceType} has no source declaration.");
        var srcDoc = solution.GetDocument(srcSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.SourceType} not found.");
        var srcFilePath = srcDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.SourceType} has no filesystem path.");

        var nullClassName = string.IsNullOrWhiteSpace(intent.NullClassName)
            ? $"Null{typeSymbol.Name}"
            : intent.NullClassName!;
        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (typeSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : typeSymbol.ContainingNamespace.ToDisplayString());

        var overridableMethods = typeSymbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary
                        && !m.IsStatic
                        && (m.IsVirtual || m.IsAbstract || m.IsOverride))
            .ToList();

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("// Null Object for ").Append(typeSymbol.Name).AppendLine(" — safe defaults, no-ops on void, `default` on values.");
        sb.Append("public sealed class ").Append(nullClassName).Append(" : ").AppendLine(typeSymbol.Name);
        sb.AppendLine("{");
        sb.Append("    public static readonly ").Append(nullClassName).Append(" Instance = new ").Append(nullClassName).AppendLine("();");
        sb.Append("    private ").Append(nullClassName).AppendLine("() { }");
        for (int i = 0; i < overridableMethods.Count; i++)
        {
            var m = overridableMethods[i];
            var returnType = m.ReturnType.ToDisplayString();
            sb.AppendLine();
            sb.Append("    public override ").Append(returnType).Append(' ').Append(m.Name).Append('(');
            for (int p = 0; p < m.Parameters.Length; p++)
            {
                if (p > 0) sb.Append(", ");
                sb.Append(m.Parameters[p].Type.ToDisplayString()).Append(' ').Append(m.Parameters[p].Name);
            }
            sb.AppendLine(")");
            sb.AppendLine("    {");
            if (m.ReturnsVoid)
            {
                sb.AppendLine("        // no-op");
            }
            else
            {
                sb.Append("        return default(").Append(returnType).AppendLine(");");
            }
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");

        var newFilePath = Path.Combine(Path.GetDirectoryName(srcFilePath)!, $"{nullClassName}.cs");
        var newText = sb.ToString();
        var project = srcDoc.Project.AddDocument(
            name: $"{nullClassName}.cs",
            text: newText,
            folders: srcDoc.Folders,
            filePath: newFilePath);
        var changes = new List<DocumentChange>
        {
            new(newFilePath, DocumentChangeKind.Added, OldText: null, NewText: newText),
        };
        return (project.Project.Solution, changes);
    }

    // Replace Nested Conditional with Guard Clauses — Fowler Simplifying
    // Conditionals. Selection points at an IfStatementSyntax with an else.
    // If one branch is a single `return;` / `return expr;` / `throw expr;`,
    // that branch becomes a guard clause and the other branch's contents
    // move up one level (out of the else / then). The condition is inverted
    // when it's the then-branch that gets hoisted (guard was the else).
    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyReplaceNestedConditionalWithGuardClausesAsync(
        MsSolution solution,
        ReplaceNestedConditionalWithGuardClausesIntent intent,
        CancellationToken cancellationToken)
    {
        var containingSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, intent.ContainingMember, cancellationToken).ConfigureAwait(false)
            as IMethodSymbol
            ?? throw new InvalidOperationException($"Containing method not found: {intent.ContainingMember.Signature}");
        var syntaxRef = containingSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no source declaration.");
        var doc = solution.GetDocument(syntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for containing method not found.");
        var filePath = doc.FilePath
            ?? throw new InvalidOperationException($"{intent.ContainingMember} has no filesystem path.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var span = TextSpan.FromBounds(intent.SelectionStart, intent.SelectionStart + intent.SelectionLength);
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var ifStmt = node.AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException("Selection does not cover an if-statement.");
        if (ifStmt.Else is null)
            throw new NotSupportedException("Guard-clause conversion requires the if to have an else clause.");
        if (ifStmt.Parent is not BlockSyntax parentBlock)
            throw new NotSupportedException("Enclosing scope must be a block for the hoisted statements to live in.");

        static bool IsGuardStatement(StatementSyntax s) => s is ReturnStatementSyntax or ThrowStatementSyntax;

        static IReadOnlyList<StatementSyntax> Unwrap(StatementSyntax s) =>
            s is BlockSyntax block ? block.Statements.ToArray() : new[] { s };

        var thenStmts = Unwrap(ifStmt.Statement);
        var elseStmts = Unwrap(ifStmt.Else.Statement);

        bool thenIsGuard = thenStmts.Count == 1 && IsGuardStatement(thenStmts[0]);
        bool elseIsGuard = elseStmts.Count == 1 && IsGuardStatement(elseStmts[0]);

        if (!thenIsGuard && !elseIsGuard)
            throw new InvalidOperationException("Neither branch is a single return/throw — no guard clause pattern to apply.");

        IfStatementSyntax guardIf;
        IReadOnlyList<StatementSyntax> hoisted;

        if (thenIsGuard)
        {
            // Guard already in the natural place — strip `else`, hoist else's contents.
            guardIf = ifStmt.WithElse(null)
                .WithAdditionalAnnotations(Formatter.Annotation);
            hoisted = elseStmts;
        }
        else
        {
            // Else is the guard — invert condition, hoist then's contents.
            var invertedCond = InvertCondition(ifStmt.Condition);
            guardIf = SyntaxFactory.IfStatement(invertedCond, ifStmt.Else.Statement)
                .WithTriviaFrom(ifStmt)
                .WithAdditionalAnnotations(Formatter.Annotation);
            hoisted = thenStmts;
        }

        // Rebuild the enclosing block: replace the old if with [guardIf, ...hoisted].
        var newParentStmts = parentBlock.Statements.ToList();
        var idx = newParentStmts.IndexOf(ifStmt);
        newParentStmts.RemoveAt(idx);
        int insertAt = idx;
        newParentStmts.Insert(insertAt++, guardIf);
        foreach (var s in hoisted)
            newParentStmts.Insert(insertAt++, s.WithoutTrivia());
        var updatedParent = parentBlock.WithStatements(SyntaxFactory.List(newParentStmts))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(parentBlock, updatedParent);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();
        if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, oldText, newText));
        return (formatted.Project.Solution, changes);
    }

    // Boolean inversion tuned for readability rather than mechanical `!(...)`:
    // strips a top-level `!`, flips `==`/`!=` etc. Everything else is
    // wrapped in a single `!(...)` — the user can rewrite for style later.
    private static ExpressionSyntax InvertCondition(ExpressionSyntax cond)
    {
        cond = cond.WithoutTrivia();
        if (cond is ParenthesizedExpressionSyntax p) return InvertCondition(p.Expression);
        if (cond is PrefixUnaryExpressionSyntax { OperatorToken.RawKind: (int)SyntaxKind.ExclamationToken } neg)
            return neg.Operand.WithoutTrivia();
        if (cond is BinaryExpressionSyntax bin)
        {
            SyntaxKind? flipped = bin.OperatorToken.Kind() switch
            {
                SyntaxKind.EqualsEqualsToken => SyntaxKind.ExclamationEqualsToken,
                SyntaxKind.ExclamationEqualsToken => SyntaxKind.EqualsEqualsToken,
                SyntaxKind.LessThanToken => SyntaxKind.GreaterThanEqualsToken,
                SyntaxKind.LessThanEqualsToken => SyntaxKind.GreaterThanToken,
                SyntaxKind.GreaterThanToken => SyntaxKind.LessThanEqualsToken,
                SyntaxKind.GreaterThanEqualsToken => SyntaxKind.LessThanToken,
                _ => null,
            };
            if (flipped is { } f)
            {
                var kind = f switch
                {
                    SyntaxKind.EqualsEqualsToken => SyntaxKind.EqualsExpression,
                    SyntaxKind.ExclamationEqualsToken => SyntaxKind.NotEqualsExpression,
                    SyntaxKind.LessThanToken => SyntaxKind.LessThanExpression,
                    SyntaxKind.LessThanEqualsToken => SyntaxKind.LessThanOrEqualExpression,
                    SyntaxKind.GreaterThanToken => SyntaxKind.GreaterThanExpression,
                    SyntaxKind.GreaterThanEqualsToken => SyntaxKind.GreaterThanOrEqualExpression,
                    _ => SyntaxKind.None,
                };
                return SyntaxFactory.BinaryExpression(kind, bin.Left.WithoutTrivia(), SyntaxFactory.Token(f), bin.Right.WithoutTrivia());
            }
        }
        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(cond));
    }

    private static (List<StatementSyntax> Statements, List<ISymbol> Params) ExtractBranchStatementsAndParams(
        SemanticModel semantic, StatementSyntax branch, string label)
    {
        static bool IsParamOrLocal(ISymbol s) => s is ILocalSymbol or IParameterSymbol;

        // Branch can be a single statement or a Block — flatten to a list.
        var statements = branch is BlockSyntax block
            ? block.Statements.ToList()
            : new List<StatementSyntax> { branch };
        if (statements.Count == 0)
            throw new InvalidOperationException($"{label} branch is empty — nothing to extract.");

        var flow = semantic.AnalyzeDataFlow(statements[0], statements[^1])
            ?? throw new InvalidOperationException($"Data flow analysis failed for the {label} branch.");
        if (!flow.Succeeded)
            throw new InvalidOperationException($"Data flow analysis reported failure on the {label} branch.");

        var outFlow = flow.DataFlowsOut.Where(IsParamOrLocal).Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();
        if (outFlow.Count > 0)
            throw new NotSupportedException($"{label} branch has variables that flow out ({string.Join(", ", outFlow.Select(s => s.Name))}). MVP Decompose Conditional supports void branches only.");

        var parms = flow.DataFlowsIn.Where(IsParamOrLocal).Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();
        return (statements, parms);
    }

    private static MethodDeclarationSyntax BuildParameterlessOrParameterizedMethod(
        string name,
        TypeSyntax returnType,
        IReadOnlyList<ISymbol> parms,
        BlockSyntax? body,
        ExpressionSyntax? arrowBody,
        bool isStatic)
    {
        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (isStatic) modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        var paramList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parms.Select(p =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                .WithType(SyntaxFactory.ParseTypeName(TypeToDisplayString(p))))));

        var method = SyntaxFactory.MethodDeclaration(returnType, name)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(paramList);

        if (arrowBody is not null)
        {
            method = method
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(arrowBody))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else if (body is not null)
        {
            method = method.WithBody(body);
        }
        return method.WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static string TypeToDisplayString(ISymbol sym) => sym switch
    {
        ILocalSymbol l => l.Type.ToDisplayString(),
        IParameterSymbol p => p.Type.ToDisplayString(),
        _ => "object",
    };

    private static string BuildAbstractRootSource(string namespaceName, string className)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("public abstract class ").AppendLine(className);
        sb.AppendLine("{");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildSubclassSourceWithOverrides(
        string namespaceName,
        string subclassName,
        string parentName,
        IReadOnlyList<(string Name, string ParamList, string ReturnType)> virtualizedSignatures)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("public class ").Append(subclassName).Append(" : ").AppendLine(parentName);
        sb.AppendLine("{");
        for (int i = 0; i < virtualizedSignatures.Count; i++)
        {
            var (name, paramList, returnType) = virtualizedSignatures[i];
            sb.Append("    public override ").Append(returnType).Append(' ').Append(name).Append(paramList).AppendLine();
            sb.AppendLine("    {");
            sb.Append("        throw new System.NotImplementedException(\"TODO: implement ")
              .Append(name).Append(" for ").Append(subclassName).AppendLine(".\");");
            sb.AppendLine("    }");
            if (i < virtualizedSignatures.Count - 1) sb.AppendLine();
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static async Task<List<DocumentChange>> ApplyReplaceSubclassWithFieldsAsync(
        MsSolution solution,
        ReplaceSubclassWithFieldsIntent intent,
        CancellationToken cancellationToken)
    {
        var parentSymbol = await SymbolResolver.ResolveAsync(solution, intent.ParentType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Parent type not found: {intent.ParentType}");
        var parentSyntaxRef = parentSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.ParentType} has no source declaration.");
        var parentDoc = solution.GetDocument(parentSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.ParentType} not found.");
        var parentFilePath = parentDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.ParentType} has no filesystem path.");
        var parentOldText = (await parentDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var changes = new List<DocumentChange>();

        // Strip `abstract` from parent if present.
        var parentRoot = await parentDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Parent root unavailable.");
        var parentClass = parentRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == parentSymbol.Name)
            ?? throw new InvalidOperationException($"Parent class {parentSymbol.Name} not found.");
        var abstractToken = parentClass.Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.AbstractKeyword));
        if (abstractToken != default)
        {
            var newModifiers = parentClass.Modifiers.Remove(abstractToken);
            var newParent = parentClass.WithModifiers(newModifiers)
                .WithAdditionalAnnotations(Formatter.Annotation);
            var newParentRoot = parentRoot.ReplaceNode(parentClass, newParent);
            var newParentDoc = parentDoc.WithSyntaxRoot(newParentRoot);
            var formatted = await Formatter.FormatAsync(newParentDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newParentText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            if (!string.Equals(parentOldText, newParentText, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(parentFilePath, DocumentChangeKind.Modified, parentOldText, newParentText));
            }
        }

        // Delete each subclass file.
        foreach (var subRef in intent.SubclassesToRemove)
        {
            var subSymbol = await SymbolResolver.ResolveAsync(solution, subRef, null, cancellationToken).ConfigureAwait(false)
                as INamedTypeSymbol;
            if (subSymbol is null) continue;
            var subSyntaxRef = subSymbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (subSyntaxRef is null) continue;
            var subDoc = solution.GetDocument(subSyntaxRef.SyntaxTree);
            var subFilePath = subDoc?.FilePath;
            if (string.IsNullOrEmpty(subFilePath)) continue;
            var subOldText = (await subDoc!.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            changes.Add(new DocumentChange(subFilePath, DocumentChangeKind.Deleted, OldText: subOldText, NewText: null));
        }

        if (changes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nothing to change: parent {parentSymbol.Name} isn't abstract and no subclasses were resolved.");
        }
        return changes;
    }

    private static async Task<List<DocumentChange>> ApplyPreserveWholeObjectAsync(
        MsSolution solution,
        PreserveWholeObjectIntent intent,
        CancellationToken cancellationToken)
    {
        var (methodNode, _, doc, oldText) = await ResolveMethodAsync(solution, intent.OwnerType, intent.Method, cancellationToken);
        var filePath = doc.FilePath ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");

        // Verify each name-to-replace actually exists.
        var replaced = new HashSet<string>(intent.ReplacedParameterNames, StringComparer.Ordinal);
        var missing = replaced.Where(n =>
            !methodNode.ParameterList.Parameters.Any(p => p.Identifier.ValueText == n)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"These parameters are not on the method and can't be replaced: {string.Join(", ", missing)}");
        }

        // Find the insertion index (first removed position).
        var firstRemovedIndex = -1;
        for (var i = 0; i < methodNode.ParameterList.Parameters.Count; i++)
        {
            if (replaced.Contains(methodNode.ParameterList.Parameters[i].Identifier.ValueText))
            {
                firstRemovedIndex = i;
                break;
            }
        }
        if (firstRemovedIndex < 0)
        {
            throw new InvalidOperationException("No parameters selected for replacement.");
        }

        var objectTypeName = ExtractShortName(intent.ObjectType.FullyQualifiedName);
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(intent.ParameterName))
            .WithType(SyntaxFactory.ParseTypeName(objectTypeName));

        var kept = methodNode.ParameterList.Parameters
            .Where(p => !replaced.Contains(p.Identifier.ValueText))
            .ToList();
        kept.Insert(System.Math.Min(firstRemovedIndex, kept.Count), newParam);
        var newParamList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(kept));

        MethodDeclarationSyntax rewritten = methodNode.WithParameterList(newParamList);
        if (methodNode.Body is not null)
        {
            var rewriter = new ParameterAccessRewriter(replaced, intent.ParameterName);
            rewritten = rewritten.WithBody((BlockSyntax)rewriter.Visit(methodNode.Body));
        }
        else if (methodNode.ExpressionBody is not null)
        {
            var rewriter = new ParameterAccessRewriter(replaced, intent.ParameterName);
            var newExpr = (ExpressionSyntax)rewriter.Visit(methodNode.ExpressionBody.Expression);
            rewritten = rewritten.WithExpressionBody(methodNode.ExpressionBody.WithExpression(newExpr));
        }
        rewritten = rewritten.WithAdditionalAnnotations(Formatter.Annotation);

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var newRoot = root.ReplaceNode(methodNode, rewritten);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(filePath, DocumentChangeKind.Modified, oldText, newText),
        };
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyReplaceArrayWithObjectAsync(
        MsSolution solution,
        ReplaceArrayWithObjectIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var fieldName = ExtractMemberNameFromSignature(intent.ArrayField.Signature);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        MemberDeclarationSyntax? memberNode = null;
        foreach (var f in ownerClass.Members.OfType<FieldDeclarationSyntax>())
        {
            if (f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName))
            {
                memberNode = f;
                break;
            }
        }
        memberNode ??= ownerClass.Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.ValueText == fieldName);
        if (memberNode is null)
        {
            throw new InvalidOperationException($"Field/property '{fieldName}' not found on {ownerSymbol.Name}.");
        }

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (ownerSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : ownerSymbol.ContainingNamespace.ToDisplayString());
        var newClassText = BuildArrayObjectSource(namespaceName, intent.NewClassName, intent.FieldMappings);
        var newClassFilePath = Path.Combine(
            Path.GetDirectoryName(ownerFilePath)!,
            $"{intent.NewClassName}.cs");

        var newType = SyntaxFactory.ParseTypeName(intent.NewClassName).WithTrailingTrivia(SyntaxFactory.Space);
        MemberDeclarationSyntax updatedMember = memberNode switch
        {
            FieldDeclarationSyntax field => field.WithDeclaration(field.Declaration.WithType(newType))
                .WithAdditionalAnnotations(Formatter.Annotation),
            PropertyDeclarationSyntax prop => prop.WithType(newType).WithAdditionalAnnotations(Formatter.Annotation),
            _ => throw new InvalidOperationException("Unexpected member kind."),
        };

        var newRoot = root.ReplaceNode(memberNode, updatedMember);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var newProject = formatted.Project.AddDocument(
            name: $"{intent.NewClassName}.cs",
            text: newClassText,
            folders: ownerDoc.Folders,
            filePath: newClassFilePath).Project;

        var changes = new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
            new(newClassFilePath, DocumentChangeKind.Added, OldText: null, NewText: newClassText),
        };
        return (newProject.Solution, changes);
    }

    private static string BuildArrayObjectSource(
        string namespaceName,
        string className,
        IReadOnlyList<ArrayFieldMapping> mappings)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("public class ").AppendLine(className);
        sb.AppendLine("{");
        foreach (var m in mappings.OrderBy(m => m.Index))
        {
            sb.Append("    // Was array index ").Append(m.Index).AppendLine();
            sb.Append("    public ").Append(m.FieldType).Append(' ').Append(m.FieldName).AppendLine(" { get; set; }");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static async Task<List<DocumentChange>> ApplyChangeValueToReferenceAsync(
        MsSolution solution,
        ChangeValueToReferenceIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        var typeName = ownerSymbol.Name;
        var registryText = $"private static readonly System.Collections.Generic.Dictionary<{intent.KeyType}, {typeName}> {intent.RegistryFieldName} = new();";
        var registryDecl = SyntaxFactory.ParseMemberDeclaration(registryText)
            ?? throw new InvalidOperationException($"Failed to parse registry declaration: {registryText}");
        registryDecl = registryDecl.WithAdditionalAnnotations(Formatter.Annotation);

        var factoryText = $$"""
public static {{typeName}} {{intent.FactoryName}}({{intent.KeyType}} key, System.Func<{{typeName}}> factory)
{
    if (!{{intent.RegistryFieldName}}.TryGetValue(key, out var value))
    {
        value = factory();
        {{intent.RegistryFieldName}}[key] = value;
    }
    return value;
}
""";
        var factoryDecl = SyntaxFactory.ParseMemberDeclaration(factoryText)
            ?? throw new InvalidOperationException($"Failed to parse factory declaration: {factoryText}");
        factoryDecl = factoryDecl.WithAdditionalAnnotations(Formatter.Annotation);

        var newClass = ownerClass
            .WithMembers(ownerClass.Members.Insert(0, (MemberDeclarationSyntax)registryDecl).Add((MemberDeclarationSyntax)factoryDecl))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(ownerClass, newClass);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyReplaceTypeCodeWithClassAsync(
        MsSolution solution,
        ReplaceTypeCodeWithClassIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var fieldName = ExtractMemberNameFromSignature(intent.Field.Signature);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        // Locate the field or property that holds the type code.
        MemberDeclarationSyntax? memberNode = null;
        TypeSyntax? existingType = null;
        foreach (var f in ownerClass.Members.OfType<FieldDeclarationSyntax>())
        {
            if (f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName))
            {
                memberNode = f;
                existingType = f.Declaration.Type;
                break;
            }
        }
        if (memberNode is null)
        {
            var prop = ownerClass.Members.OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(p => p.Identifier.ValueText == fieldName);
            if (prop is not null)
            {
                memberNode = prop;
                existingType = prop.Type;
            }
        }
        if (memberNode is null || existingType is null)
        {
            throw new InvalidOperationException($"Field/property '{fieldName}' not found on {ownerSymbol.Name}.");
        }

        // Build the new class source with a private ctor + static readonly instances per code.
        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (ownerSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : ownerSymbol.ContainingNamespace.ToDisplayString());
        var newClassText = BuildTypeCodeClassSource(namespaceName, intent.NewClassName, intent.InnerCodeType, intent.Codes);
        var newClassFilePath = Path.Combine(
            Path.GetDirectoryName(ownerFilePath)!,
            $"{intent.NewClassName}.cs");

        // Change the field / property's type to the new class.
        var newType = SyntaxFactory.ParseTypeName(intent.NewClassName).WithTrailingTrivia(SyntaxFactory.Space);
        MemberDeclarationSyntax updatedMember;
        if (memberNode is FieldDeclarationSyntax field)
        {
            updatedMember = field.WithDeclaration(field.Declaration.WithType(newType))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }
        else
        {
            var prop = (PropertyDeclarationSyntax)memberNode;
            updatedMember = prop.WithType(newType).WithAdditionalAnnotations(Formatter.Annotation);
        }

        var newRoot = root.ReplaceNode(memberNode, updatedMember);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var newDocInProject = formatted.Project.AddDocument(
            name: $"{intent.NewClassName}.cs",
            text: newClassText,
            folders: ownerDoc.Folders,
            filePath: newClassFilePath);

        var changes = new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
            new(newClassFilePath, DocumentChangeKind.Added, OldText: null, NewText: newClassText),
        };
        return (newDocInProject.Project.Solution, changes);
    }

    private static string BuildTypeCodeClassSource(
        string namespaceName,
        string className,
        string innerCodeType,
        IReadOnlyList<TypeCodeEntry> codes)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("public sealed class ").AppendLine(className);
        sb.AppendLine("{");
        foreach (var c in codes)
        {
            sb.Append("    public static readonly ").Append(className).Append(' ').Append(c.Name)
              .Append(" = new(").Append(c.Value).AppendLine(");");
        }
        sb.AppendLine();
        sb.Append("    public ").Append(innerCodeType).AppendLine(" Code { get; }");
        sb.AppendLine();
        sb.Append("    private ").Append(className).Append('(').Append(innerCodeType).AppendLine(" code)");
        sb.AppendLine("    {");
        sb.AppendLine("        Code = code;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static async Task<List<DocumentChange>> ApplySelfEncapsulateFieldAsync(
        MsSolution solution,
        SelfEncapsulateFieldIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var fieldName = ExtractMemberNameFromSignature(intent.Field.Signature);
        var propertyName = string.IsNullOrEmpty(intent.PropertyName)
            ? PascalCase(fieldName)
            : intent.PropertyName;

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        // Locate the field.
        FieldDeclarationSyntax? fieldDecl = null;
        VariableDeclaratorSyntax? declarator = null;
        foreach (var f in ownerClass.Members.OfType<FieldDeclarationSyntax>())
        {
            var match = f.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == fieldName);
            if (match is not null)
            {
                fieldDecl = f;
                declarator = match;
                break;
            }
        }
        if (fieldDecl is null || declarator is null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {ownerSymbol.Name}.");
        }

        var fieldType = fieldDecl.Declaration.Type;
        var accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[]
        {
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(fieldName)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(fieldName),
                        SyntaxFactory.IdentifierName("value"))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
        }));
        var propertyDecl = SyntaxFactory.PropertyDeclaration(fieldType, propertyName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(accessors)
            .WithAdditionalAnnotations(Formatter.Annotation);

        // Rewrite internal accesses to `fieldName` → `propertyName`, but skip the field
        // declaration itself and the getter/setter body of the new property.
        var newClass = ownerClass.AddMembers(propertyDecl).WithAdditionalAnnotations(Formatter.Annotation);
        var rewriter = new SelfEncapsulateFieldRewriter(fieldName, propertyName);
        var rewrittenClass = (ClassDeclarationSyntax)rewriter.Visit(newClass);

        var newRoot = root.ReplaceNode(ownerClass, rewrittenClass);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static string PascalCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var trimmed = s.TrimStart('_');
        if (trimmed.Length == 0) return s;
        return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }

    private sealed class SelfEncapsulateFieldRewriter : CSharpSyntaxRewriter
    {
        private readonly string _fieldName;
        private readonly string _propertyName;

        public SelfEncapsulateFieldRewriter(string fieldName, string propertyName)
        {
            _fieldName = fieldName;
            _propertyName = propertyName;
        }

        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) => node;
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (node.Identifier.ValueText == _propertyName) return node;
            return base.VisitPropertyDeclaration(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.ValueText != _fieldName) return base.VisitIdentifierName(node);
            if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node) return node;
            if (node.Parent is VariableDeclaratorSyntax) return node;
            if (node.Parent is ParameterSyntax) return node;
            return SyntaxFactory.IdentifierName(_propertyName).WithTriviaFrom(node);
        }
    }

    private static async Task<List<DocumentChange>> ApplyChangeReferenceToValueAsync(
        MsSolution solution,
        ChangeReferenceToValueIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        var newMembers = new List<MemberDeclarationSyntax>();
        var modified = false;
        foreach (var member in ownerClass.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field
                    when !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)
                                                   || m.IsKind(SyntaxKind.ConstKeyword)):
                {
                    var newModifiers = field.Modifiers.Add(
                        SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                    newMembers.Add(field.WithModifiers(newModifiers).WithAdditionalAnnotations(Formatter.Annotation));
                    modified = true;
                    break;
                }
                case PropertyDeclarationSyntax prop when prop.AccessorList is not null:
                {
                    var setter = prop.AccessorList.Accessors.FirstOrDefault(a =>
                        a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));
                    if (setter is null || setter.IsKind(SyntaxKind.InitAccessorDeclaration))
                    {
                        newMembers.Add(prop);
                        break;
                    }
                    var initAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    var newAccessorList = prop.AccessorList.WithAccessors(
                        prop.AccessorList.Accessors.Replace(setter, initAccessor));
                    newMembers.Add(prop.WithAccessorList(newAccessorList).WithAdditionalAnnotations(Formatter.Annotation));
                    modified = true;
                    break;
                }
                default:
                    newMembers.Add(member);
                    break;
            }
        }

        if (!modified)
        {
            throw new InvalidOperationException(
                $"{ownerSymbol.Name} already appears immutable — no mutable fields or set accessors to lock down.");
        }

        var newClass = ownerClass.WithMembers(SyntaxFactory.List(newMembers))
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newRoot = root.ReplaceNode(ownerClass, newClass);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyReplaceDataValueWithObjectAsync(
        MsSolution solution,
        ReplaceDataValueWithObjectIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var fieldName = ExtractMemberNameFromSignature(intent.Field.Signature);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        // Find the target field: either a FieldDeclarationSyntax with matching variable,
        // or a PropertyDeclarationSyntax with matching identifier.
        FieldDeclarationSyntax? fieldDecl = null;
        VariableDeclaratorSyntax? declarator = null;
        foreach (var f in ownerClass.Members.OfType<FieldDeclarationSyntax>())
        {
            var match = f.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == fieldName);
            if (match is not null)
            {
                fieldDecl = f;
                declarator = match;
                break;
            }
        }
        PropertyDeclarationSyntax? propDecl = null;
        if (fieldDecl is null)
        {
            propDecl = ownerClass.Members.OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(p => p.Identifier.ValueText == fieldName);
        }
        if (fieldDecl is null && propDecl is null)
        {
            throw new InvalidOperationException($"Field/property '{fieldName}' not found on {ownerSymbol.Name}.");
        }

        string primitiveType;
        if (fieldDecl is not null)
        {
            if (fieldDecl.Declaration.Variables.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Field '{fieldName}' shares its declaration with siblings — split declarations first.");
            }
            primitiveType = fieldDecl.Declaration.Type.ToString();
        }
        else
        {
            primitiveType = propDecl!.Type.ToString();
        }

        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (ownerSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : ownerSymbol.ContainingNamespace.ToDisplayString());
        var wrapperText = BuildWrapperClassSource(namespaceName, intent.WrapperClassName, primitiveType, intent.InnerFieldName);
        var wrapperFilePath = Path.Combine(
            Path.GetDirectoryName(ownerFilePath)!,
            $"{intent.WrapperClassName}.cs");

        // Change the field's/property's type to the wrapper.
        var newType = SyntaxFactory.ParseTypeName(intent.WrapperClassName).WithTrailingTrivia(SyntaxFactory.Space);
        MemberDeclarationSyntax updatedMember;
        if (fieldDecl is not null)
        {
            var newDeclaration = fieldDecl.Declaration.WithType(newType);
            updatedMember = fieldDecl.WithDeclaration(newDeclaration).WithAdditionalAnnotations(Formatter.Annotation);
        }
        else
        {
            updatedMember = propDecl!.WithType(newType).WithAdditionalAnnotations(Formatter.Annotation);
        }

        SyntaxNode targetNode = fieldDecl ?? (SyntaxNode)propDecl!;
        var newRoot = root.ReplaceNode(targetNode, updatedMember);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var wrapperDoc = formatted.Project.AddDocument(
            name: $"{intent.WrapperClassName}.cs",
            text: wrapperText,
            folders: ownerDoc.Folders,
            filePath: wrapperFilePath);

        var changes = new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
            new(wrapperFilePath, DocumentChangeKind.Added, OldText: null, NewText: wrapperText),
        };

        return (wrapperDoc.Project.Solution, changes);
    }

    private static string BuildWrapperClassSource(
        string namespaceName,
        string className,
        string primitiveType,
        string innerFieldName)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
        sb.Append("public class ").AppendLine(className);
        sb.AppendLine("{");
        sb.Append("    public ").Append(primitiveType).Append(' ').Append(innerFieldName).AppendLine(" { get; set; }");
        sb.AppendLine();
        sb.Append("    public ").Append(className).Append('(').Append(primitiveType).Append(' ').Append(innerFieldName.ToLowerInvariant()).AppendLine(")");
        sb.AppendLine("    {");
        sb.Append("        ").Append(innerFieldName).Append(" = ").Append(innerFieldName.ToLowerInvariant()).AppendLine(";");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyRenameParameterAsync(
        MsSolution solution,
        RenameParameterIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var methodSymbol = ownerSymbol.GetMembers().OfType<IMethodSymbol>()
            .FirstOrDefault(m => RoslynToModelMapper.ToMemberRef(m).Signature == intent.Method.Signature)
            ?? throw new InvalidOperationException($"Method '{intent.Method.Signature}' not found on {ownerSymbol.Name}.");

        var paramSymbol = methodSymbol.Parameters.FirstOrDefault(p => p.Name == intent.OldName)
            ?? throw new InvalidOperationException(
                $"Parameter '{intent.OldName}' not found on {intent.OwnerType}.{intent.Method.Signature}.");

        var options = new SymbolRenameOptions();
        var newSolution = await Renamer.RenameSymbolAsync(solution, paramSymbol, options, intent.NewName, cancellationToken).ConfigureAwait(false);
        var changes = await CollectDocumentChangesAsync(solution, newSolution, cancellationToken).ConfigureAwait(false);
        return (newSolution, changes);
    }

    private static async Task<(MethodDeclarationSyntax Method, INamedTypeSymbol Owner, Document Doc, string OldText)> ResolveMethodAsync(
        MsSolution solution,
        TypeRef ownerType,
        MemberRef methodRef,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, ownerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {ownerType}");
        var methodSymbol = ownerSymbol.GetMembers().OfType<IMethodSymbol>()
            .FirstOrDefault(m => RoslynToModelMapper.ToMemberRef(m).Signature == methodRef.Signature)
            ?? throw new InvalidOperationException($"Method '{methodRef.Signature}' not found on {ownerSymbol.Name}.");
        var methodSyntaxRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"Method '{methodSymbol.Name}' has no source declaration.");
        var methodNode = await methodSyntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false)
            as MethodDeclarationSyntax
            ?? throw new InvalidOperationException($"Method syntax for '{methodSymbol.Name}' unavailable.");
        var doc = solution.GetDocument(methodSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {ownerType} not found.");
        var oldText = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        return (methodNode, ownerSymbol, doc, oldText);
    }

    private static async Task<List<DocumentChange>> ApplyAddParameterAsync(
        MsSolution solution,
        AddParameterIntent intent,
        CancellationToken cancellationToken)
    {
        var (methodNode, _, doc, oldText) = await ResolveMethodAsync(solution, intent.OwnerType, intent.Method, cancellationToken);
        var filePath = doc.FilePath ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");

        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(intent.ParameterName))
            .WithType(SyntaxFactory.ParseTypeName(intent.ParameterType));
        if (!string.IsNullOrEmpty(intent.DefaultValue))
        {
            newParam = newParam.WithDefault(
                SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(intent.DefaultValue)));
        }

        var newParams = methodNode.ParameterList.AddParameters(newParam);
        var newMethod = methodNode.WithParameterList(newParams)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var newRoot = root.ReplaceNode(methodNode, newMethod);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(filePath, DocumentChangeKind.Modified, oldText, newText),
        };
    }

    private static async Task<List<DocumentChange>> ApplyRemoveParameterAsync(
        MsSolution solution,
        RemoveParameterIntent intent,
        CancellationToken cancellationToken)
    {
        var (methodNode, _, doc, oldText) = await ResolveMethodAsync(solution, intent.OwnerType, intent.Method, cancellationToken);
        var filePath = doc.FilePath ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");

        var target = methodNode.ParameterList.Parameters
            .FirstOrDefault(p => p.Identifier.ValueText == intent.ParameterName)
            ?? throw new InvalidOperationException(
                $"Parameter '{intent.ParameterName}' not found on {intent.OwnerType}.{intent.Method.Signature}.");

        var newParamList = methodNode.ParameterList.WithParameters(
            methodNode.ParameterList.Parameters.Remove(target));
        var newMethod = methodNode.WithParameterList(newParamList)
            .WithAdditionalAnnotations(Formatter.Annotation);

        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Root unavailable.");
        var newRoot = root.ReplaceNode(methodNode, newMethod);
        var newDoc = doc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(filePath, DocumentChangeKind.Modified, oldText, newText),
        };
    }

    private static async Task<List<DocumentChange>> ApplyChangeBidirectionalToUnidirectionalAsync(
        MsSolution solution,
        ChangeBidirectionalToUnidirectionalIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var fieldName = ExtractMemberNameFromSignature(intent.Field.Signature);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Owner class {ownerSymbol.Name} not found.");

        // Try field first, then property (auto-property version of the same field name).
        MemberDeclarationSyntax? nodeToRemove = null;
        foreach (var f in ownerClass.Members.OfType<FieldDeclarationSyntax>())
        {
            if (f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName))
            {
                nodeToRemove = f;
                break;
            }
        }
        nodeToRemove ??= ownerClass.Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.ValueText == fieldName);

        if (nodeToRemove is null)
        {
            throw new InvalidOperationException(
                $"Field or property '{fieldName}' not found on {ownerSymbol.Name}.");
        }

        var newRoot = root.RemoveNode(nodeToRemove,
            SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepEndOfLine) ?? root;
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static async Task<(MsSolution NewSolution, List<DocumentChange> Changes)> ApplyIntroduceParameterObjectAsync(
        MsSolution solution,
        IntroduceParameterObjectIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");

        var methodSymbol = ownerSymbol.GetMembers().OfType<IMethodSymbol>()
            .FirstOrDefault(m => RoslynToModelMapper.ToMemberRef(m).Signature == intent.Method.Signature)
            ?? throw new InvalidOperationException($"Method '{intent.Method.Signature}' not found on {ownerSymbol.Name}.");
        if (methodSymbol.Parameters.Length == 0)
        {
            throw new InvalidOperationException($"Method '{methodSymbol.Name}' has no parameters to bundle.");
        }

        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var methodRef = methodSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"Method '{methodSymbol.Name}' has no source declaration.");
        var methodNode = await methodRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false)
            as MethodDeclarationSyntax
            ?? throw new InvalidOperationException($"Method syntax for '{methodSymbol.Name}' unavailable.");

        // Collect existing parameter names to rewrite inside the body.
        var paramNames = new HashSet<string>(methodSymbol.Parameters.Select(p => p.Name), StringComparer.Ordinal);
        var paramTypeByName = methodSymbol.Parameters.ToDictionary(p => p.Name, p => p.Type.ToDisplayString(), StringComparer.Ordinal);

        // Build the parameter object class source.
        var namespaceName = intent.TargetNamespace?.FullName
                            ?? (ownerSymbol.ContainingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : ownerSymbol.ContainingNamespace.ToDisplayString());
        var poText = BuildParameterObjectSource(namespaceName, intent.ProposedObjectName, methodSymbol.Parameters);
        var poFilePath = Path.Combine(
            Path.GetDirectoryName(ownerFilePath)!,
            $"{intent.ProposedObjectName}.cs");

        // Rewrite the method: replace parameter list with a single parameter, rewrite body identifiers.
        var newParamList = SyntaxFactory.ParameterList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(intent.ParameterName))
                    .WithType(SyntaxFactory.ParseTypeName(intent.ProposedObjectName))));

        MethodDeclarationSyntax rewrittenMethod = methodNode.WithParameterList(newParamList);
        if (methodNode.Body is not null)
        {
            var rewriter = new ParameterAccessRewriter(paramNames, intent.ParameterName);
            var newBody = (BlockSyntax)rewriter.Visit(methodNode.Body);
            rewrittenMethod = rewrittenMethod.WithBody(newBody);
        }
        else if (methodNode.ExpressionBody is not null)
        {
            var rewriter = new ParameterAccessRewriter(paramNames, intent.ParameterName);
            var newExpr = (ExpressionSyntax)rewriter.Visit(methodNode.ExpressionBody.Expression);
            rewrittenMethod = rewrittenMethod.WithExpressionBody(methodNode.ExpressionBody.WithExpression(newExpr));
        }
        rewrittenMethod = rewrittenMethod.WithAdditionalAnnotations(Formatter.Annotation);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var newRoot = root.ReplaceNode(methodNode, rewrittenMethod);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var newProject = formatted.Project.AddDocument(
            name: $"{intent.ProposedObjectName}.cs",
            text: poText,
            folders: ownerDoc.Folders,
            filePath: poFilePath).Project;

        var changes = new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
            new(poFilePath, DocumentChangeKind.Added, OldText: null, NewText: poText),
        };

        return (newProject.Solution, changes);
    }

    private static string BuildParameterObjectSource(
        string namespaceName,
        string className,
        IReadOnlyList<IParameterSymbol> parameters)
    {
        var sb = new StringBuilder();

        // Collect distinct namespaces to import.
        var usings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            AddTypeNamespace(usings, p.Type, namespaceName);
        }
        foreach (var ns in usings)
        {
            sb.Append("using ").Append(ns).AppendLine(";");
        }
        if (usings.Count > 0) sb.AppendLine();

        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append("public class ").AppendLine(className);
        sb.AppendLine("{");
        foreach (var p in parameters)
        {
            sb.Append("    public ").Append(p.Type.ToDisplayString(InterfaceMemberFormat))
              .Append(' ').Append(p.Name).AppendLine(";");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private sealed class ParameterAccessRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _paramNames;
        private readonly string _objName;

        public ParameterAccessRewriter(HashSet<string> paramNames, string objName)
        {
            _paramNames = paramNames;
            _objName = objName;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!_paramNames.Contains(node.Identifier.ValueText))
            {
                return base.VisitIdentifierName(node);
            }
            // Skip declarations (parameter/local names): only rewrite when the identifier is
            // used as a value reference.
            if (node.Parent is ParameterSyntax) return node;
            if (node.Parent is VariableDeclaratorSyntax) return node;
            if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node) return node;

            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(_objName),
                node.WithoutTrivia())
                .WithTriviaFrom(node);
        }
    }

    private static async Task<List<DocumentChange>> ApplyReplaceConstructorWithFactoryAsync(
        MsSolution solution,
        ReplaceConstructorWithFactoryIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        var ctor = ownerClass.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault()
            ?? throw new InvalidOperationException($"{ownerSymbol.Name} has no constructor to convert.");

        var typeName = ownerSymbol.Name;
        var paramList = ctor.ParameterList;
        var argList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                paramList.Parameters.Select(p =>
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Identifier)))));

        var creationExpr = SyntaxFactory.ObjectCreationExpression(
            SyntaxFactory.ParseTypeName(typeName),
            argList,
            initializer: null);

        var factoryModifiers = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword));

        var factoryMethod = SyntaxFactory.MethodDeclaration(
                returnType: SyntaxFactory.ParseTypeName(typeName),
                identifier: SyntaxFactory.Identifier(intent.FactoryName))
            .WithModifiers(factoryModifiers)
            .WithParameterList(paramList.WithoutTrivia())
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(creationExpr))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newClass = ownerClass;
        if (intent.MakeConstructorPrivate)
        {
            var newModifiers = SyntaxFactory.TokenList(
                ctor.Modifiers
                    .Where(m => !m.IsKind(SyntaxKind.PublicKeyword)
                                && !m.IsKind(SyntaxKind.InternalKeyword)
                                && !m.IsKind(SyntaxKind.ProtectedKeyword)
                                && !m.IsKind(SyntaxKind.PrivateKeyword))
                    .Prepend(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));
            var newCtor = ctor.WithModifiers(newModifiers).WithAdditionalAnnotations(Formatter.Annotation);
            newClass = newClass.ReplaceNode(ctor, newCtor);
        }
        newClass = newClass.AddMembers(factoryMethod).WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(ownerClass, newClass);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static async Task<List<DocumentChange>> ApplyReplaceMagicNumberAsync(
        MsSolution solution,
        ReplaceMagicNumberIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Class {ownerSymbol.Name} not found.");

        // Find all numeric literal expressions within this class whose token text matches the target.
        var literalNodes = ownerClass.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.Token.Text == intent.LiteralValue)
            .ToArray();

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var lit in literalNodes)
        {
            replacements[lit] = SyntaxFactory.IdentifierName(intent.ConstantName)
                .WithTriviaFrom(lit)
                .WithAdditionalAnnotations(Formatter.Annotation);
        }
        var updatedClass = replacements.Count > 0
            ? ownerClass.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig])
            : ownerClass;

        var constFieldText = $"private const {intent.ConstantType} {intent.ConstantName} = {intent.LiteralValue};";
        var constDecl = SyntaxFactory.ParseMemberDeclaration(constFieldText)
            ?? throw new InvalidOperationException($"Failed to parse constant declaration: {constFieldText}");
        constDecl = constDecl.WithAdditionalAnnotations(Formatter.Annotation);

        // Insert the const as the first member so it appears at the top.
        updatedClass = updatedClass.WithMembers(
            updatedClass.Members.Insert(0, (MemberDeclarationSyntax)constDecl))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(ownerClass, updatedClass);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static async Task<List<DocumentChange>> ApplyEncapsulateFieldAsync(
        MsSolution solution,
        EncapsulateFieldIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");
        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var fieldName = ExtractMemberNameFromSignature(intent.Field.Signature);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Owner class {ownerSymbol.Name} not found.");

        FieldDeclarationSyntax? fieldDecl = null;
        VariableDeclaratorSyntax? declarator = null;
        foreach (var f in ownerClass.Members.OfType<FieldDeclarationSyntax>())
        {
            var match = f.Declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == fieldName);
            if (match is not null)
            {
                fieldDecl = f;
                declarator = match;
                break;
            }
        }
        if (fieldDecl is null || declarator is null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on {ownerSymbol.Name}.");
        }
        if (fieldDecl.Declaration.Variables.Count > 1)
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' shares its declaration with siblings — split declarations first before encapsulating.");
        }

        var fieldType = fieldDecl.Declaration.Type;
        var modifiers = fieldDecl.Modifiers;

        var accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(new[]
        {
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
        }));

        var propertyNode = SyntaxFactory.PropertyDeclaration(fieldType, fieldName)
            .WithModifiers(modifiers)
            .WithAccessorList(accessors);

        // Preserve initializer if present, as an EqualsValueClause on the property.
        if (declarator.Initializer is not null)
        {
            propertyNode = propertyNode
                .WithInitializer(declarator.Initializer)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        propertyNode = propertyNode.WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(fieldDecl, propertyNode);
        var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
        };
    }

    private static async Task<List<DocumentChange>> ApplyRemoveSettingMethodAsync(
        MsSolution solution,
        RemoveSettingMethodIntent intent,
        CancellationToken cancellationToken)
    {
        var ownerSymbol = await SymbolResolver.ResolveAsync(solution, intent.OwnerType, null, cancellationToken).ConfigureAwait(false)
            as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Owner type not found: {intent.OwnerType}");

        var ownerSyntaxRef = ownerSymbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no source declaration.");
        var ownerDoc = solution.GetDocument(ownerSyntaxRef.SyntaxTree)
            ?? throw new InvalidOperationException($"Document for {intent.OwnerType} not found.");
        var ownerFilePath = ownerDoc.FilePath
            ?? throw new InvalidOperationException($"{intent.OwnerType} has no filesystem path.");
        var ownerOldText = (await ownerDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var propName = ExtractMemberNameFromSignature(intent.Property.Signature);

        var root = await ownerDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Owner root unavailable.");
        var ownerClass = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == ownerSymbol.Name)
            ?? throw new InvalidOperationException($"Owner class {ownerSymbol.Name} not found.");

        var propNode = ownerClass.Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.ValueText == propName);
        if (propNode is not null)
        {
            var accessorList = propNode.AccessorList
                ?? throw new InvalidOperationException($"Property {propName} has no accessor list.");
            var setter = accessorList.Accessors.FirstOrDefault(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration))
                ?? throw new InvalidOperationException($"Property {propName} has no setter to remove.");
            var newAccessorList = accessorList.WithAccessors(accessorList.Accessors.Remove(setter));
            var newProp = propNode.WithAccessorList(newAccessorList).WithAdditionalAnnotations(Formatter.Annotation);
            var newRoot = root.ReplaceNode(propNode, newProp);
            var newDoc = ownerDoc.WithSyntaxRoot(newRoot);
            var formatted = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            var newText = (await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

            return new List<DocumentChange>
            {
                new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newText),
            };
        }

        // No matching property — try to remove a matching setter method (name starts with "Set").
        var setterMethodName = propName.StartsWith("Set", StringComparison.Ordinal) ? propName : "Set" + propName;
        var methodNode = ownerClass.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == setterMethodName)
            ?? throw new InvalidOperationException($"No property or setter method '{propName}' found on {ownerSymbol.Name}.");
        var rootWithoutMethod = root.RemoveNode(methodNode,
            SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepEndOfLine) ?? root;
        var docWithoutMethod = ownerDoc.WithSyntaxRoot(rootWithoutMethod);
        var formattedM = await Formatter.FormatAsync(docWithoutMethod, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newTextM = (await formattedM.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        return new List<DocumentChange>
        {
            new(ownerFilePath, DocumentChangeKind.Modified, ownerOldText, newTextM),
        };
    }

    private static async Task<(MsSolution NewSolution, DocumentChange? Change)> AddInterfaceToBaseListAsync(
        MsSolution solution,
        Document classDocument,
        INamedTypeSymbol typeSymbol,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        var root = await classDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Source root unavailable.");
        var oldText = (await classDocument.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var classNode = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == typeSymbol.Name);
        if (classNode is null)
        {
            return (solution, null);
        }

        var newBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));
        BaseListSyntax newBaseList = classNode.BaseList is null
            ? SyntaxFactory.BaseList(
                SyntaxFactory.Token(SyntaxKind.ColonToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(newBaseType))
            : classNode.BaseList.AddTypes(newBaseType);

        var newClassNode = classNode
            .WithIdentifier(classNode.Identifier.WithTrailingTrivia(SyntaxFactory.Space))
            .WithBaseList(newBaseList)
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newRoot = root.ReplaceNode(classNode, newClassNode);

        var newDoc = classDocument.WithSyntaxRoot(newRoot);
        var formattedDoc = await Formatter.FormatAsync(newDoc, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var newText = (await formattedDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();

        var change = new DocumentChange(
            FilePath: classDocument.FilePath ?? string.Empty,
            Kind: DocumentChangeKind.Modified,
            OldText: oldText,
            NewText: newText);

        return (formattedDoc.Project.Solution, change);
    }

    private static string BuildInterfaceSource(string namespaceName, string interfaceName, IReadOnlyList<ISymbol> members)
    {
        var sb = new StringBuilder();
        var hasNamespace = !string.IsNullOrEmpty(namespaceName);

        var usings = CollectUsedNamespaces(members, namespaceName);
        foreach (var ns in usings)
        {
            sb.Append("using ").Append(ns).AppendLine(";");
        }
        if (usings.Count > 0)
        {
            sb.AppendLine();
        }

        if (hasNamespace)
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append("public interface ").AppendLine(interfaceName);
        sb.AppendLine("{");
        foreach (var member in members)
        {
            var signature = RenderInterfaceMember(member);
            if (signature is null)
            {
                continue;
            }
            sb.Append("    ").AppendLine(signature);
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static IReadOnlyList<string> CollectUsedNamespaces(
        IReadOnlyList<ISymbol> members,
        string currentNamespace)
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            switch (member)
            {
                case IMethodSymbol method:
                    AddTypeNamespace(namespaces, method.ReturnType, currentNamespace);
                    foreach (var p in method.Parameters)
                    {
                        AddTypeNamespace(namespaces, p.Type, currentNamespace);
                    }
                    break;
                case IPropertySymbol prop:
                    AddTypeNamespace(namespaces, prop.Type, currentNamespace);
                    break;
                case IEventSymbol evt:
                    AddTypeNamespace(namespaces, evt.Type, currentNamespace);
                    break;
            }
        }
        return namespaces.ToList();
    }

    private static void AddTypeNamespace(
        SortedSet<string> namespaces,
        ITypeSymbol type,
        string currentNamespace)
    {
        switch (type)
        {
            case INamedTypeSymbol named:
                var ns = named.ContainingNamespace;
                if (ns is not null && !ns.IsGlobalNamespace)
                {
                    var nsName = ns.ToDisplayString();
                    if (!string.Equals(nsName, currentNamespace, StringComparison.Ordinal))
                    {
                        namespaces.Add(nsName);
                    }
                }
                foreach (var arg in named.TypeArguments)
                {
                    AddTypeNamespace(namespaces, arg, currentNamespace);
                }
                break;
            case IArrayTypeSymbol arr:
                AddTypeNamespace(namespaces, arr.ElementType, currentNamespace);
                break;
        }
    }

    private static readonly SymbolDisplayFormat InterfaceMemberFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions:
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeParamsRefOut |
            SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static string? RenderInterfaceMember(ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                return method.ToDisplayString(InterfaceMemberFormat) + ";";

            case IPropertySymbol { IsIndexer: false } property:
            {
                var accessors = new StringBuilder();
                if (property.GetMethod is not null && property.GetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    accessors.Append("get; ");
                }
                if (property.SetMethod is not null && property.SetMethod.DeclaredAccessibility == Accessibility.Public)
                {
                    accessors.Append("set; ");
                }
                if (accessors.Length == 0)
                {
                    return null;
                }
                var type = property.Type.ToDisplayString(InterfaceMemberFormat);
                return $"{type} {property.Name} {{ {accessors.ToString().TrimEnd()} }}";
            }

            case IEventSymbol evt:
            {
                var type = evt.Type.ToDisplayString(InterfaceMemberFormat);
                return $"event {type} {evt.Name};";
            }

            default:
                return null;
        }
    }

    private static (MsSolution NewSolution, List<DocumentChange> Changes) ApplyAddGhostType(
        MsSolution solution,
        AddGhostTypeIntent intent)
    {
        var nsFull = intent.Namespace.FullName ?? string.Empty;

        Project? target = null;
        var bestMatchLen = -1;
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            var rootNs = project.Name;
            if (nsFull == rootNs || nsFull.StartsWith(rootNs + ".", StringComparison.Ordinal))
            {
                if (rootNs.Length > bestMatchLen)
                {
                    bestMatchLen = rootNs.Length;
                    target = project;
                }
            }
        }

        if (target is null)
        {
            throw new InvalidOperationException(
                $"Cannot find a project whose name matches namespace '{nsFull}'.");
        }

        var projDir = Path.GetDirectoryName(target.FilePath)
                      ?? throw new InvalidOperationException($"Project '{target.Name}' has no directory.");

        var subNs = nsFull.Length > bestMatchLen ? nsFull.Substring(bestMatchLen + 1) : string.Empty;
        var folders = string.IsNullOrEmpty(subNs)
            ? Array.Empty<string>()
            : subNs.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var fileDir = folders.Length == 0 ? projDir : Path.Combine(projDir, Path.Combine(folders));
        var filePath = Path.Combine(fileDir, $"{intent.ProposedName}.cs");

        var text = BuildGhostTypeSource(nsFull, intent.ProposedName, intent.Kind);

        var newDoc = target.AddDocument(
            name: $"{intent.ProposedName}.cs",
            text: text,
            folders: folders,
            filePath: filePath);

        var change = new DocumentChange(
            FilePath: filePath,
            Kind: DocumentChangeKind.Added,
            OldText: null,
            NewText: text);

        return (newDoc.Project.Solution, new List<DocumentChange> { change });
    }

    private static string BuildGhostTypeSource(string namespaceName, string typeName, Kata.Core.Model.TypeKind kind)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }

        switch (kind)
        {
            case Kata.Core.Model.TypeKind.Enum:
                sb.Append("public enum ").AppendLine(typeName);
                sb.AppendLine("{");
                sb.AppendLine("}");
                break;

            case Kata.Core.Model.TypeKind.Record:
                sb.Append("public record ").Append(typeName).AppendLine("();");
                break;

            case Kata.Core.Model.TypeKind.Interface:
                sb.Append("public interface ").AppendLine(typeName);
                sb.AppendLine("{");
                sb.AppendLine("}");
                break;

            case Kata.Core.Model.TypeKind.Struct:
                sb.Append("public struct ").AppendLine(typeName);
                sb.AppendLine("{");
                sb.AppendLine("}");
                break;

            default:
                sb.Append("public class ").AppendLine(typeName);
                sb.AppendLine("{");
                sb.AppendLine("}");
                break;
        }

        return sb.ToString();
    }

    private static async Task<List<DocumentChange>> CollectDocumentChangesAsync(
        MsSolution oldSolution,
        MsSolution newSolution,
        CancellationToken cancellationToken)
    {
        var changes = new List<DocumentChange>();

        foreach (var newProject in newSolution.Projects)
        {
            var oldProject = oldSolution.GetProject(newProject.Id);
            if (oldProject is null)
            {
                continue;
            }

            var diff = newProject.GetChanges(oldProject);
            foreach (var docId in diff.GetChangedDocuments())
            {
                var oldDoc = oldProject.GetDocument(docId);
                var newDoc = newProject.GetDocument(docId);
                if (oldDoc is null || newDoc is null)
                {
                    continue;
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (string.Equals(oldText, newText, StringComparison.Ordinal))
                {
                    continue;
                }

                changes.Add(new DocumentChange(
                    FilePath: newDoc.FilePath ?? string.Empty,
                    Kind: DocumentChangeKind.Modified,
                    OldText: oldText,
                    NewText: newText));
            }
        }

        return changes;
    }

    public async Task<SmellIndex> DetectSmellsAsync(
        SolutionModel model,
        CancellationToken cancellationToken = default)
    {
        var all = new List<CodeSmell>();

        // C# 側: universal + Roslyn 固有
        if (_solution is not null)
        {
            var csAnalyzer = new Analysis.SmellAnalyzer();
            var csIndex = await csAnalyzer.AnalyzeAsync(_solution, model, cancellationToken)
                .ConfigureAwait(false);
            all.AddRange(csIndex.All);
        }

        // C++/CLI 側: universal のみ (CppCompilation が存在するときだけ)
        if (_cppCompilation is not null)
        {
            var cppAnalyzer = new Kata.Cpp.Analysis.CppSmellAnalyzer();
            var cppIndex = cppAnalyzer.Analyze(_cppCompilation, model, cancellationToken);
            all.AddRange(cppIndex.All);
        }

        return new SmellIndex(all);
    }
}
