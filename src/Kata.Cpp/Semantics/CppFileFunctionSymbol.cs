namespace Kata.Cpp.Semantics;

/// <summary>
/// A free / static function defined at namespace or global scope inside a
/// C++/CLI implementation file (no `Type::` qualifier). Not owned by any
/// <see cref="CppTypeSymbol"/> — indexed by the file it lives in.
///
/// パラメータ / 返り値のテキストは <see cref="CppImplementationLocator"/> が best-effort で
/// 抽出した生テキスト。UI が擬似型メンバーとして表示するのに使う。
/// </summary>
public sealed class CppFileFunctionSymbol
{
    public string Name { get; }
    public int ParameterCount { get; }
    public CppDeclarationSite Site { get; }
    public string ParameterListText { get; }
    public string ReturnTypeText { get; }

    internal CppFileFunctionSymbol(
        string name,
        int parameterCount,
        CppDeclarationSite site,
        string parameterListText = "",
        string returnTypeText = "")
    {
        Name = name;
        ParameterCount = parameterCount;
        Site = site;
        ParameterListText = parameterListText;
        ReturnTypeText = returnTypeText;
    }
}
