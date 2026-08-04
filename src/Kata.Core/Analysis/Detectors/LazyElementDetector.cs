using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// Fowler: "lazy element" — 少なすぎて存在意義が薄い型 / メソッド。
// 型レベル: 何もメンバーを持たない具象クラス/構造体。
// メンバーレベル: 数行以下で自分と同名のメンバーへ委譲しているメソッド (delegator の疑い)。
public sealed class LazyElementDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.LazyElement;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();

            if (type.Kind is TypeKind.Class or TypeKind.Struct
                && !type.IsAbstract
                && !type.IsStatic
                && type.Members.Count == 0)
            {
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, Member: null,
                    "empty class — inline or delete");
            }

            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                if (m.Kind is MemberKind.Constructor) continue;

                var lines = context.GetBodyLineCount(m.Ref);
                if (lines == 0 || lines > SmellThresholds.LazyElementMaxBodyLines) continue;

                var body = context.GetBodyText(m.Ref);
                if (body is null || !body.Contains(m.Name)) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, m.Ref,
                    $"trivial {lines}-line delegator — consider inlining");
            }
        }
    }
}
