using Kata.Core.Analysis.Detectors;

namespace Kata.Core.Analysis;

/// <summary>
/// 言語非依存 detector の既定セット。Roslyn / Cpp どちらの adapter からも同じ
/// リストで呼ばれる。表示順序はここの enum 順序に従う。
/// </summary>
public static class UniversalDetectorRegistry
{
    public static IEnumerable<IUniversalSmellDetector> Default()
    {
        // 型構造ベース
        yield return new LargeClassDetector();
        yield return new DataClassDetector();
        yield return new LazyElementDetector();
        yield return new RefusedBequestDetector();
        yield return new AlternativeClassesWithDifferentInterfacesDetector();
        yield return new SpeculativeGeneralityDetector();
        yield return new TemporaryFieldDetector();
        yield return new GlobalDataDetector();
        yield return new MutableDataDetector();
        yield return new DataClumpsDetector();
        yield return new PrimitiveObsessionDetector();
        yield return new MysteriousNameDetector();

        // git 履歴 (未実装、空を返す stub)
        yield return new DivergentChangeDetector();
        yield return new ShotgunSurgeryDetector();

        // body 系
        yield return new LongFunctionDetector();
        yield return new LongParameterListDetector();
        yield return new LoopsDetector();
        yield return new CommentsDetector();
        yield return new DuplicatedCodeDetector();
        yield return new RepeatedSwitchesDetector();
        yield return new MessageChainsDetector();
    }
}
