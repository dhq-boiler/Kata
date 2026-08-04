using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

public sealed class LargeClassDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.LargeClass;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            var count = type.Members.Count;
            if (count <= SmellThresholds.LargeClassMembers) continue;

            yield return new CodeSmell(
                Category,
                SmellSeverity.Warning,
                type.Ref,
                Member: null,
                $"{count} members (>{SmellThresholds.LargeClassMembers})");
        }
    }
}
