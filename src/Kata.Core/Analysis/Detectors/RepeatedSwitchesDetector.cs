using System.Text.RegularExpressions;
using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// 型全体の source を走査し、同じ switch (expr) / switch expr =&gt; ... の "expr" が
// 2 回以上出てくるものを Info で報告する。polymorphism 化候補の signal。
public sealed class RepeatedSwitchesDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.RepeatedSwitches;

    // switch キーワード直後の 1 個の括弧付き式 (単純用途) を拾う。case ラベルは無視。
    // switch expression 形式 (`x switch { ... }`) にも大雑把に効くよう `switch\s*{` 前の識別子も拾う。
    private static readonly Regex SwitchStmt = new(@"\bswitch\s*\(\s*([^)]+?)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SwitchExpr = new(@"([A-Za-z_][\w\.]*)\s+switch\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            var text = context.GetTypeText(type.Ref);
            if (string.IsNullOrEmpty(text)) continue;

            var keys = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in SwitchStmt.Matches(text)) Bump(keys, m.Groups[1].Value.Trim());
            foreach (Match m in SwitchExpr.Matches(text)) Bump(keys, m.Groups[1].Value.Trim());

            foreach (var kv in keys)
            {
                if (kv.Value < 2) continue;
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, Member: null,
                    $"switch on `{kv.Key}` repeated {kv.Value}× — consider polymorphism");
            }
        }
    }

    private static void Bump(Dictionary<string, int> d, string k)
    {
        if (string.IsNullOrEmpty(k)) return;
        d[k] = d.TryGetValue(k, out var c) ? c + 1 : 1;
    }
}
