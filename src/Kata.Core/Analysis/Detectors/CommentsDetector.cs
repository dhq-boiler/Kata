using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// method body 内のコメント数 (//... と /* ... */) を数える。閾値以上で Info。
// 命名リファクタで解ける可能性を示唆する。
public sealed class CommentsDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.Comments;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                var body = context.GetBodyText(m.Ref);
                if (string.IsNullOrEmpty(body)) continue;
                var count = CountComments(body);
                if (count < SmellThresholds.CommentsPerMethod) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, m.Ref,
                    $"{count} comment(s) in body");
            }
        }
    }

    // 素朴なスキャン。文字列リテラル内の // や /* は無視できていないが、
    // Info の閾値以上でしか鳴らないので false positive の noise は許容範囲。
    private static int CountComments(string body)
    {
        var count = 0;
        var i = 0;
        while (i < body.Length - 1)
        {
            if (body[i] == '/' && body[i + 1] == '/')
            {
                count++;
                while (i < body.Length && body[i] != '\n') i++;
            }
            else if (body[i] == '/' && body[i + 1] == '*')
            {
                count++;
                i += 2;
                while (i < body.Length - 1 && !(body[i] == '*' && body[i + 1] == '/')) i++;
                i += 2;
            }
            else i++;
        }
        return count;
    }
}
