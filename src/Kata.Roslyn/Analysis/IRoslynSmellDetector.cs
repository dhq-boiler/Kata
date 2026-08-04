using Kata.Core.Analysis;

namespace Kata.Roslyn.Analysis;

// Roslyn-side detector. Kept internal — Kata.Core sees only ICodeSmellDetector (category tag).
internal interface IRoslynSmellDetector : ICodeSmellDetector
{
    IEnumerable<CodeSmell> Detect(RoslynSmellContext context, CancellationToken ct);
}
