using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// private mutable field で、代入 1 回・読み 0-1 回のもの — local に降ろせる可能性が高い。
// 保守的に textual scanning。
public sealed class TemporaryFieldDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.TemporaryField;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();

            var typeSource = context.GetTypeText(type.Ref);
            if (string.IsNullOrEmpty(typeSource)) continue;

            foreach (var m in type.Members)
            {
                if (m.Kind != MemberKind.Field) continue;
                if (m.IsStatic || m.IsReadOnly) continue;
                if (m.Accessibility != MemberAccessibility.Private) continue;

                var name = m.Name;
                var writes = CountOccurrences(typeSource, name + " =")
                             + CountOccurrences(typeSource, name + "=");
                var total = CountOccurrences(typeSource, name);
                var reads = total - writes;

                if (writes == 1 && reads <= 1)
                {
                    yield return new CodeSmell(
                        Category, SmellSeverity.Info, type.Ref, m.Ref,
                        "assigned once, read at most once — could be a local");
                }
            }
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
