using Kata.Core.Analysis;
using Kata.Core.Model;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Cpp.Analysis;

/// <summary>
/// Cpp/CLI 型に対して universal detector を走らせるための <see cref="ISmellContext"/> 実装。
///
/// - <see cref="HandwrittenTypes"/>: SolutionModel の cpp-cli プロジェクトの型を返す
/// - <see cref="GetBodyText"/>: メンバーの ImplementationSite (.cpp 側) から
///   `{ ... }` を brace-matching で切り出して返す
/// - <see cref="GetTypeText"/>: 型の DeclarationSite (.h 側) から class body 全体を切り出す
///
/// noise 許容: string / char / コメント内の brace は無視していない。smell heuristic なので false
/// positive よりカバレッジを優先。将来精度を上げるなら CppCliLexer を使って skip する。
/// </summary>
public sealed class CppSmellContext : ISmellContext
{
    private readonly CppCompilation _compilation;
    private readonly Dictionary<TypeRef, TypeModel> _typeModels;
    private readonly Dictionary<TypeRef, CppTypeSymbol> _typeSymbols;
    private readonly Dictionary<MemberRef, CppMemberSymbol> _memberByRef;
    private readonly Dictionary<string, string> _sourceByFile;
    private readonly Dictionary<TypeRef, string?> _typeTextCache = new();
    private readonly Dictionary<MemberRef, string?> _bodyCache = new();

    public SolutionModel Model { get; }
    public string LanguageId => "cpp-cli";

    public CppSmellContext(CppCompilation compilation, SolutionModel model)
    {
        _compilation = compilation;
        Model = model;

        _typeModels = new();
        foreach (var project in model.Projects)
        {
            if (!string.Equals(project.LanguageId, "cpp-cli", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var type in project.Types)
                _typeModels[type.Ref] = type;
        }

        _typeSymbols = new();
        foreach (var t in compilation.AllTypes)
            _typeSymbols[new TypeRef(t.FullyQualifiedName)] = t;

        _memberByRef = new();
        foreach (var t in compilation.AllTypes)
        {
            var typeRef = new TypeRef(t.FullyQualifiedName);
            foreach (var m in t.Members)
            {
                var memberRef = new MemberRef(typeRef, m.Signature);
                _memberByRef[memberRef] = m;
            }
        }

        _sourceByFile = new(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
            _sourceByFile[tree.FilePath] = tree.SourceText;
        foreach (var tree in compilation.ImplementationTrees)
            _sourceByFile[tree.FilePath] = tree.SourceText;
    }

    public IEnumerable<TypeModel> HandwrittenTypes
    {
        get
        {
            foreach (var kv in _typeModels)
            {
                if (kv.Value.IsGhost) continue;
                if (!_typeSymbols.ContainsKey(kv.Key)) continue;
                yield return kv.Value;
            }
        }
    }

    public string? GetBodyText(MemberRef member)
    {
        if (_bodyCache.TryGetValue(member, out var cached)) return cached;
        var body = ComputeBodyText(member);
        _bodyCache[member] = body;
        return body;
    }

    public int GetBodyLineCount(MemberRef member)
    {
        var body = GetBodyText(member);
        if (string.IsNullOrEmpty(body)) return 0;
        var count = 1;
        foreach (var c in body) if (c == '\n') count++;
        return count;
    }

    public string? GetTypeText(TypeRef type)
    {
        if (_typeTextCache.TryGetValue(type, out var cached)) return cached;
        var text = ComputeTypeText(type);
        _typeTextCache[type] = text;
        return text;
    }

    public bool TryGetType(TypeRef typeRef, out TypeModel? type)
    {
        if (_typeModels.TryGetValue(typeRef, out var m)) { type = m; return true; }
        type = null;
        return false;
    }

    // -----

    private string? ComputeBodyText(MemberRef member)
    {
        if (!_memberByRef.TryGetValue(member, out var sym)) return null;
        // ImplementationSite (.cpp 側) が本命。inline body は header にあるので DeclarationSite で見る。
        var site = sym.ImplementationSite ?? sym.DeclarationSite;
        if (!_sourceByFile.TryGetValue(site.FilePath, out var source)) return null;
        var startFromName = site.Span.Start + site.Span.Length;
        return ExtractBracedBlock(source, startFromName);
    }

    private string? ComputeTypeText(TypeRef typeRef)
    {
        if (!_typeSymbols.TryGetValue(typeRef, out var sym)) return null;
        var site = sym.DeclarationSite;
        if (!_sourceByFile.TryGetValue(site.FilePath, out var source)) return null;
        var startFromName = site.Span.Start + site.Span.Length;
        return ExtractBracedBlock(source, startFromName);
    }

    // 指定位置以降で最初の '{' を見つけ、対応する '}' までを本文として返す (両端含む)。
    // 見つからなければ null。string/char/comment 内の brace は誤検知しうるが許容。
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
}
