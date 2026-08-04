namespace Kata.Roslyn.Analysis.Detectors;

// Roslyn semantic model が必要な detector だけを持つ。
// 汎用 (model + body-text) で判定可能なやつは Kata.Core.Analysis.UniversalDetectorRegistry へ移住済み。
internal static class Registry
{
    public static IEnumerable<IRoslynSmellDetector> DefaultDetectors()
    {
        yield return new FeatureEnvyDetector();
        yield return new InsiderTradingDetector();
        yield return new MiddleManDetector();
    }
}
