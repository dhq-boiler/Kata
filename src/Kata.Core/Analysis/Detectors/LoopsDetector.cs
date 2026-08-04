using System.Text.RegularExpressions;
using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// body の for / while / foreach / do を単語境界の regex で数える。
// LINQ / pipeline に置き換える余地の signal を投げるだけなので、少しでもあれば Info。
//
// C++/CLI は skip: Fowler の "loops → pipeline / LINQ" は C#/Java 特有の勧告で、
// C++/CLI ではループが慣用句 (LINQ 相当が言語標準に無く、STL <ranges> は限定的、
// リアルタイム系のホットパスでは GC pause を避けるためむしろループが正解)。
// 別言語のリファクタ勧告を鳴らすと user が AI に相談 → 「これは false positive
// っす」と一蹴される循環になるので、そもそも鳴らさない。
public sealed class LoopsDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.Loops;

    private static readonly Regex LoopPattern = new(@"\b(for|foreach|while|do)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        if (string.Equals(context.LanguageId, "cpp-cli", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                var body = context.GetBodyText(m.Ref);
                if (string.IsNullOrEmpty(body)) continue;
                var count = LoopPattern.Matches(body).Count;
                if (count == 0) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, m.Ref,
                    $"{count} loop(s) — consider LINQ / pipeline");
            }
        }
    }
}
