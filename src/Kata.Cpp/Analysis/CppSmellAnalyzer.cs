using Kata.Core.Analysis;
using Kata.Core.Model;
using Kata.Cpp.Semantics;

namespace Kata.Cpp.Analysis;

/// <summary>
/// C++/CLI 側 smell aggregator。universal detector 群を CppSmellContext 上で走らせる。
/// Roslyn 側と対称。Cpp 固有 detector (semantic 濃いもの) は追加時に本 class に足す。
/// </summary>
public sealed class CppSmellAnalyzer
{
    private readonly IReadOnlyList<IUniversalSmellDetector> _detectors;

    public CppSmellAnalyzer(IEnumerable<IUniversalSmellDetector>? detectors = null)
    {
        _detectors = (detectors ?? UniversalDetectorRegistry.Default()).ToList();
    }

    public SmellIndex Analyze(CppCompilation compilation, SolutionModel model, CancellationToken ct)
    {
        var context = new CppSmellContext(compilation, model);
        var all = new List<CodeSmell>();
        foreach (var detector in _detectors)
        {
            ct.ThrowIfCancellationRequested();
            try { all.AddRange(detector.Detect(context, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* 1 detector の失敗で全体を止めない */ }
        }
        return new SmellIndex(all);
    }
}
