using System.Text;
using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// メソッド body を空白除去で正規化し、同一のものを 2 個以上検出したら Warning。
// 短すぎる body は noise (throw NotImplemented 等) なので長さ閾値でフィルタ。
public sealed class DuplicatedCodeDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.DuplicatedCode;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        var buckets = new Dictionary<string, List<(TypeRef T, MemberRef M)>>(StringComparer.Ordinal);

        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                var body = context.GetBodyText(m.Ref);
                if (string.IsNullOrEmpty(body)) continue;
                var norm = Normalize(body);
                if (norm.Length < SmellThresholds.DuplicatedCodeMinChars) continue;
                if (!buckets.TryGetValue(norm, out var list))
                    buckets[norm] = list = new();
                list.Add((type.Ref, m.Ref));
            }
        }

        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;
            foreach (var (t, m) in bucket)
            {
                // RelatedMembers に「自分以外の全ての duplicate 先」を積んで
                // AI 提案時に「全 N 箇所を呼び出しに置換」できるようにする。
                var others = new List<MemberRef>(bucket.Count - 1);
                foreach (var (_, om) in bucket)
                {
                    if (!om.Equals(m)) others.Add(om);
                }
                yield return new CodeSmell(
                    Category, SmellSeverity.Warning, t, m,
                    $"body identical to {bucket.Count - 1} other method(s)",
                    RelatedMembers: others);
            }
        }
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }
}
