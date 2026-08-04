using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// 型名 / メンバー名が読解性ヒューリスティックを通らないものを Info で吊り上げる。
// 除外: ctor / dtor / property accessor (Kind で既にフィルタ済み)
public sealed class MysteriousNameDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.MysteriousName;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            if (SmellDetectorHelpers.IsMysteriousName(type.Name))
            {
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, Member: null,
                    $"type name \"{type.Name}\" is not descriptive");
            }

            foreach (var m in type.Members)
            {
                if (m.Kind is MemberKind.Constructor) continue;
                if (!SmellDetectorHelpers.IsMysteriousName(m.Name)) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, m.Ref,
                    $"name \"{m.Name}\" is not descriptive");
            }
        }
    }
}
