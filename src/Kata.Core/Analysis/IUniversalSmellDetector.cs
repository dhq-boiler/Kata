namespace Kata.Core.Analysis;

/// <summary>
/// 言語非依存の smell detector。<see cref="ISmellContext"/> だけを触るので
/// Roslyn 側 / Cpp 側 いずれの context に対しても走らせられる。
///
/// 特定言語の semantic model (Roslyn の INamedTypeSymbol 等) が必要な detector は
/// この interface ではなく Roslyn/Cpp 内部の adapter 固有 interface を使うこと。
/// </summary>
public interface IUniversalSmellDetector : ICodeSmellDetector
{
    IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct);
}
