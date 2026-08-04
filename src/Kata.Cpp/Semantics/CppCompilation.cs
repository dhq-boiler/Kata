using Kata.Core.Model;
using Kata.Cpp.Syntax;

namespace Kata.Cpp.Semantics;

public sealed class CppCompilation
{
    private readonly Dictionary<string, CppTypeSymbol> _byFqn;
    private readonly ILookup<string, CppTypeSymbol> _bySimpleName;

    public IReadOnlyList<CppSyntaxTree> SyntaxTrees { get; }
    public IReadOnlyList<CppSyntaxTree> ImplementationTrees { get; }
    public IReadOnlyList<CppTypeSymbol> AllTypes { get; }

    /// <summary>
    /// File-level (free / static) functions defined at namespace or global scope
    /// inside each implementation file, keyed by absolute file path.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CppFileFunctionSymbol>> FileFunctionsByFilePath { get; }

    /// <summary>
    /// Preprocessor macros (<c>#define</c>) extracted from headers and impl files,
    /// keyed by absolute file path. Include-guard sentinels are filtered out.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CppMacroSymbol>> FileMacrosByFilePath { get; }

    private CppCompilation(
        IReadOnlyList<CppSyntaxTree> syntaxTrees,
        IReadOnlyList<CppSyntaxTree> implementationTrees,
        IReadOnlyList<CppTypeSymbol> allTypes,
        Dictionary<string, CppTypeSymbol> byFqn,
        ILookup<string, CppTypeSymbol> bySimpleName,
        IReadOnlyDictionary<string, IReadOnlyList<CppFileFunctionSymbol>> fileFunctions,
        IReadOnlyDictionary<string, IReadOnlyList<CppMacroSymbol>> fileMacros)
    {
        SyntaxTrees = syntaxTrees;
        ImplementationTrees = implementationTrees;
        AllTypes = allTypes;
        _byFqn = byFqn;
        _bySimpleName = bySimpleName;
        FileFunctionsByFilePath = fileFunctions;
        FileMacrosByFilePath = fileMacros;
    }

    public static CppCompilation Create(
        IEnumerable<CppSyntaxTree> syntaxTrees,
        IEnumerable<CppSyntaxTree>? implementationTrees = null)
    {
        var trees = syntaxTrees as IReadOnlyList<CppSyntaxTree> ?? syntaxTrees.ToArray();
        var implTrees = implementationTrees is null
            ? (IReadOnlyList<CppSyntaxTree>)Array.Empty<CppSyntaxTree>()
            : (implementationTrees as IReadOnlyList<CppSyntaxTree> ?? implementationTrees.ToArray());
        var byFqn = new Dictionary<string, CppTypeSymbol>(StringComparer.Ordinal);
        var typeList = new List<CppTypeSymbol>();
        var pending = new List<(CppDeclaration Decl, CppTypeSymbol Symbol, string FilePath)>();

        foreach (var tree in trees)
        {
            foreach (var decl in tree.Declarations)
            {
                var fqn = string.IsNullOrEmpty(decl.NamespaceFullName)
                    ? decl.Name
                    : $"{decl.NamespaceFullName}.{decl.Name}";

                if (byFqn.ContainsKey(fqn))
                {
                    // Duplicate FQN across trees — Phase 2 will attach CandidateReason.PreprocessorAmbiguous.
                    continue;
                }

                var site = new CppDeclarationSite(tree.FilePath, decl.NameSpan);
                var symbol = new CppTypeSymbol(fqn, decl.Name, decl.NamespaceFullName, decl.Kind, site,
                    isAbstract: decl.IsAbstract, isSealed: decl.IsSealed);
                byFqn.Add(fqn, symbol);
                typeList.Add(symbol);
                pending.Add((decl, symbol, tree.FilePath));
            }
        }

        var bySimpleName = typeList.ToLookup(s => s.Name, StringComparer.Ordinal);

        foreach (var (decl, symbol, filePath) in pending)
        {
            symbol.FinalizeMembers(BuildMembers(decl, symbol, filePath));
            symbol.FinalizeBaseTypes(ResolveBaseTypes(decl, byFqn, bySimpleName));
        }

        var fileFunctions = new Dictionary<string, IReadOnlyList<CppFileFunctionSymbol>>(StringComparer.OrdinalIgnoreCase);
        AttachImplementationSites(implTrees, bySimpleName, fileFunctions);
        // Headers can also carry namespace-level `inline` free functions (e.g. AI-generated
        // helper headers with only a `namespace Foo { inline int Bar(...) { ... } }`). Without
        // this pass those functions never make it into FileFunctionsByFilePath, so no
        // file-scope pseudo TypeModel is created and the header is invisible on the diagram.
        // Pattern A member impl matches still no-op because AttachImplementationSites skips
        // members whose ImplementationSite is already set (the .cpp pass wins).
        AttachImplementationSites(trees, bySimpleName, fileFunctions);

        var fileMacros = ExtractMacros(trees, implTrees);

        return new CppCompilation(trees, implTrees, typeList, byFqn, bySimpleName, fileFunctions, fileMacros);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CppMacroSymbol>> ExtractMacros(
        IReadOnlyList<CppSyntaxTree> headerTrees,
        IReadOnlyList<CppSyntaxTree> implTrees)
    {
        var acc = new Dictionary<string, IReadOnlyList<CppMacroSymbol>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in headerTrees.Concat(implTrees))
        {
            var macros = CppMacroExtractor.Extract(tree.FilePath, tree.SourceText);
            if (macros.Count == 0) continue;
            // Same file appearing in both header and impl lists is unlikely (.h vs .cpp) — but be defensive.
            if (acc.TryGetValue(tree.FilePath, out var existing))
            {
                var merged = new List<CppMacroSymbol>(existing);
                merged.AddRange(macros);
                acc[tree.FilePath] = merged;
            }
            else
            {
                acc[tree.FilePath] = macros;
            }
        }
        return acc;
    }

    public static CppCompilation FromVcxProj(string vcxprojPath)
        => Create(
            CppCliProjectLoader.LoadSyntaxTrees(vcxprojPath),
            CppCliProjectLoader.LoadImplementationTrees(vcxprojPath));

    private static void AttachImplementationSites(
        IReadOnlyList<CppSyntaxTree> implTrees,
        ILookup<string, CppTypeSymbol> bySimpleName,
        Dictionary<string, IReadOnlyList<CppFileFunctionSymbol>> fileFunctions)
    {
        foreach (var tree in implTrees)
        {
            var fileFuncs = new List<CppFileFunctionSymbol>();
            foreach (var impl in CppImplementationLocator.Locate(tree.FilePath, tree.SourceText))
            {
                if (impl.TypeName is null)
                {
                    fileFuncs.Add(new CppFileFunctionSymbol(
                        name: impl.MethodName,
                        parameterCount: impl.ArgumentCount,
                        site: new CppDeclarationSite(impl.FilePath, impl.MethodNameSpan),
                        parameterListText: impl.ParameterListText,
                        returnTypeText: impl.ReturnTypeText));
                    continue;
                }

                var typeCandidates = bySimpleName[impl.TypeName].ToArray();
                if (typeCandidates.Length != 1)
                {
                    continue;
                }
                var type = typeCandidates[0];

                var memberCandidates = type.Members
                    .Where(m => string.Equals(m.Name, impl.MethodName, StringComparison.Ordinal))
                    .ToArray();
                if (memberCandidates.Length == 0)
                {
                    continue;
                }

                CppMemberSymbol? matched;
                if (memberCandidates.Length == 1)
                {
                    matched = memberCandidates[0];
                }
                else
                {
                    matched = memberCandidates.FirstOrDefault(m => m.Parameters.Count == impl.ArgumentCount);
                }

                if (matched is null || matched.ImplementationSite is not null)
                {
                    continue;
                }
                matched.AttachImplementationSite(new CppDeclarationSite(impl.FilePath, impl.MethodNameSpan));
            }
            if (fileFuncs.Count > 0)
            {
                fileFunctions[tree.FilePath] = fileFuncs;
            }
        }
    }

    public CppTypeSymbol? GetTypeByFullyQualifiedName(string fullyQualifiedName)
        => _byFqn.TryGetValue(fullyQualifiedName, out var s) ? s : null;

    /// <summary>
    /// メンバー本体 (.cpp 側の <c>{ ... }</c>) を文字列として返す。
    /// name span 直後の最初の <c>{</c> から対応する <c>}</c> までを brace-matching で
    /// 切り出す (両端 <c>{ }</c> を含む)。取れない場合は null。
    /// smell 検知や body-based の cross-lang 参照解析で使う。
    /// </summary>
    public string? TryGetMemberBody(CppMemberSymbol member)
    {
        var site = member.ImplementationSite ?? member.DeclarationSite;
        var tree = GetTreeByPath(site.FilePath);
        if (tree is null) return null;
        var startFromName = site.Span.Start + site.Span.Length;
        return ExtractBracedBlock(tree.SourceText, startFromName);
    }

    private CppSyntaxTree? GetTreeByPath(string filePath)
    {
        foreach (var t in SyntaxTrees)
            if (string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) return t;
        foreach (var t in ImplementationTrees)
            if (string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    private static string? ExtractBracedBlock(string source, int start)
    {
        var i = start;
        while (i < source.Length && source[i] != '{') i++;
        if (i >= source.Length) return null;

        var open = i;
        var depth = 0;
        for (; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }
        return null;
    }

    public CppMemberSymbolInfo ResolveMember(CppTypeSymbol type, string memberName, int? arity = null)
    {
        var candidates = new List<CppMemberSymbol>();
        var visited = new HashSet<CppTypeSymbol>();
        CollectMembers(type, memberName, arity, candidates, visited);

        return candidates.Count switch
        {
            0 => CppMemberSymbolInfo.NotFound,
            1 => CppMemberSymbolInfo.Resolved(candidates[0]),
            _ => CppMemberSymbolInfo.Ambiguous(candidates),
        };
    }

    private static void CollectMembers(
        CppTypeSymbol type,
        string memberName,
        int? arity,
        List<CppMemberSymbol> sink,
        HashSet<CppTypeSymbol> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var m in type.Members)
        {
            if (!string.Equals(m.Name, memberName, StringComparison.Ordinal))
            {
                continue;
            }
            if (arity.HasValue && MatchesByArity(m, arity.Value) is false)
            {
                continue;
            }
            sink.Add(m);
        }

        // Only recurse into bases when nothing was found on this type — subclass declarations shadow.
        if (sink.Count > 0)
        {
            return;
        }

        foreach (var b in type.BaseTypes)
        {
            CollectMembers(b, memberName, arity, sink, visited);
            if (sink.Count > 0)
            {
                return;
            }
        }
    }

    private static bool MatchesByArity(CppMemberSymbol member, int arity)
    {
        // Arity only meaningful for callable members. Fields / properties / events ignore it.
        if (member.Kind is MemberKind.Method or MemberKind.Constructor)
        {
            return member.Parameters.Count == arity;
        }
        return true;
    }

    public CppSymbolInfo ResolveType(string typeName, IEnumerable<string>? usings = null)
    {
        var canonical = typeName.Replace("::", ".");

        if (_byFqn.TryGetValue(canonical, out var direct))
        {
            return CppSymbolInfo.Resolved(direct);
        }

        // Qualified name that missed the FQN table — look up by trailing simple name.
        if (canonical.Contains('.'))
        {
            var shortName = canonical[(canonical.LastIndexOf('.') + 1)..];
            return LookupBySimpleName(shortName);
        }

        // Simple name — try each using-prefix first.
        if (usings is not null)
        {
            foreach (var u in usings)
            {
                var qualified = $"{u.Replace("::", ".")}.{canonical}";
                if (_byFqn.TryGetValue(qualified, out var viaUsing))
                {
                    return CppSymbolInfo.Resolved(viaUsing);
                }
            }
        }

        return LookupBySimpleName(canonical);
    }

    private CppSymbolInfo LookupBySimpleName(string simpleName)
    {
        var matches = _bySimpleName[simpleName].ToArray();
        return matches.Length switch
        {
            0 => CppSymbolInfo.NotFound,
            1 => CppSymbolInfo.Resolved(matches[0]),
            _ => CppSymbolInfo.Ambiguous(matches),
        };
    }

    private static IEnumerable<CppMemberSymbol> BuildMembers(
        CppDeclaration decl,
        CppTypeSymbol owner,
        string filePath)
    {
        foreach (var m in decl.Members)
        {
            var parameters = m.Parameters ?? Array.Empty<CppParameter>();
            var returnTypeDisplay = m.ReturnTypeDisplay ?? string.Empty;
            var signature = m.Kind switch
            {
                MemberKind.Method => SymbolKeyFormatter.FormatMethodSignature(
                    returnTypeDisplay,
                    m.Name,
                    parameters.Select(p => new SymbolKeyFormatter.ParameterKey(p.Type, p.Name)).ToArray()),
                MemberKind.Constructor => SymbolKeyFormatter.FormatMethodSignature(
                    returnTypeDisplay: string.Empty,
                    m.Name,
                    parameters.Select(p => new SymbolKeyFormatter.ParameterKey(p.Type, p.Name)).ToArray()),
                _ => SymbolKeyFormatter.FormatFieldSignature(m.Name),
            };
            yield return new CppMemberSymbol(
                owner,
                m.Name,
                m.Kind,
                signature,
                new CppDeclarationSite(filePath, m.NameSpan),
                m.IsStatic,
                parameters,
                returnTypeDisplay);
        }
    }

    private static IEnumerable<CppTypeSymbol> ResolveBaseTypes(
        CppDeclaration decl,
        IReadOnlyDictionary<string, CppTypeSymbol> byFqn,
        ILookup<string, CppTypeSymbol> bySimpleName)
    {
        foreach (var raw in decl.BaseTypeNames.Concat(decl.InterfaceTypeNames))
        {
            var canonical = raw.Replace("::", ".");
            if (byFqn.TryGetValue(canonical, out var direct))
            {
                yield return direct;
                continue;
            }

            var shortName = canonical.Contains('.')
                ? canonical[(canonical.LastIndexOf('.') + 1)..]
                : canonical;
            var matches = bySimpleName[shortName].ToArray();
            if (matches.Length == 1)
            {
                yield return matches[0];
            }
            // 0 or >1 — drop; ambiguity handling arrives in Phase 2.
        }
    }
}
