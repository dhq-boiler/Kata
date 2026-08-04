using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

public sealed class LongParameterListDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.LongParameterList;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var method in SmellDetectorHelpers.Methods(type))
            {
                var count = method.Parameters.Count;
                if (count <= SmellThresholds.LongParameterListCount) continue;

                yield return new CodeSmell(
                    Category,
                    SmellSeverity.Warning,
                    type.Ref,
                    method.Ref,
                    $"{count} parameters (>{SmellThresholds.LongParameterListCount})");
            }
        }
    }
}
