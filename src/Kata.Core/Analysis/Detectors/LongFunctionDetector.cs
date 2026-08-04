using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

public sealed class LongFunctionDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.LongFunction;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                var lines = context.GetBodyLineCount(m.Ref);
                if (lines <= SmellThresholds.LongFunctionLines) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Warning, type.Ref, m.Ref,
                    $"{lines} lines (>{SmellThresholds.LongFunctionLines})");
            }
        }
    }
}
