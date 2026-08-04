using Kata.Core.Analysis;
using Kata.Core.Model;
using Kata.Roslyn.Analysis.Detectors;
using MsSolution = Microsoft.CodeAnalysis.Solution;

namespace Kata.Roslyn.Analysis;

// C# 側の smell aggregator。universal detector + Roslyn 固有 detector の両方を走らせ、
// 一つの SmellIndex に merge して返す。
// 個別 detector 例外は握りつぶす — 1 つが落ちても他が走るように。
internal sealed class SmellAnalyzer
{
    private readonly IReadOnlyList<IUniversalSmellDetector> _universalDetectors;
    private readonly IReadOnlyList<IRoslynSmellDetector> _roslynDetectors;

    public SmellAnalyzer(
        IEnumerable<IUniversalSmellDetector>? universalDetectors = null,
        IEnumerable<IRoslynSmellDetector>? roslynDetectors = null)
    {
        _universalDetectors = (universalDetectors ?? UniversalDetectorRegistry.Default()).ToList();
        _roslynDetectors = (roslynDetectors ?? Registry.DefaultDetectors()).ToList();
    }

    public async Task<SmellIndex> AnalyzeAsync(
        MsSolution solution,
        SolutionModel model,
        CancellationToken ct)
    {
        var context = await RoslynSmellContext.CreateAsync(solution, model, ct).ConfigureAwait(false);
        var all = new List<CodeSmell>();

        foreach (var detector in _universalDetectors)
        {
            ct.ThrowIfCancellationRequested();
            try { all.AddRange(detector.Detect(context, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* 1 detector の失敗で全体を止めない */ }
        }

        foreach (var detector in _roslynDetectors)
        {
            ct.ThrowIfCancellationRequested();
            try { all.AddRange(detector.Detect(context, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return new SmellIndex(all);
    }
}
