using System.Text.RegularExpressions;
using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// "a.b.c.d" のような連鎖 member access。閾値以上のドット段数で Info。
// 文字列リテラル / コメント内は誤検知しうるが noise は許容 (Info)。
public sealed class MessageChainsDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.MessageChains;

    // 識別子 (.識別子){N-1} を貪欲マッチ。invocation の () や引数は跨がない前提。
    private static readonly Regex ChainPattern = new(
        @"\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*){3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                var body = context.GetBodyText(m.Ref);
                if (string.IsNullOrEmpty(body)) continue;
                var maxDepth = 0;
                foreach (Match match in ChainPattern.Matches(body))
                {
                    var depth = 1;
                    foreach (var c in match.Value) if (c == '.') depth++;
                    if (depth > maxDepth) maxDepth = depth;
                }
                if (maxDepth < SmellThresholds.MessageChainDepth) continue;
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, m.Ref,
                    $"chain depth {maxDepth} — hide the intermediate hops");
            }
        }
    }
}
