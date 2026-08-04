namespace Kata.Core.Analysis.Detectors;

// git 履歴情報が必要な detector。単発の解析パスでは判定不能なので、pipeline が
// 用意されるまでは空検出でカテゴリの席だけ占める。
public sealed class DivergentChangeDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.DivergentChange;
    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
        => Array.Empty<CodeSmell>();
}

public sealed class ShotgunSurgeryDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.ShotgunSurgery;
    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
        => Array.Empty<CodeSmell>();
}
