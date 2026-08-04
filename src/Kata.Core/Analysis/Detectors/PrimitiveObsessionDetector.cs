using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// 2 パターン:
//   - method/ctor で >= N 個の全 primitive パラメータ (bag-of-strings 傾向)
//   - 型が >= N 個の field/property を持ち、全て primitive (money-as-decimal 等)
public sealed class PrimitiveObsessionDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.PrimitiveObsession;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                if (m.Parameters.Count < SmellThresholds.PrimitiveObsessionMinCount) continue;

                var allPrimitive = m.Parameters.All(p => SmellDetectorHelpers.IsPrimitiveDisplay(p.TypeDisplay));
                if (!allPrimitive) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, m.Ref,
                    $"{m.Parameters.Count} primitive parameters — consider a value object");
            }

            var primitiveState = 0;
            var totalState = 0;
            foreach (var m in SmellDetectorHelpers.StateMembers(type))
            {
                if (m.Kind == MemberKind.Event) continue;
                totalState++;
                if (SmellDetectorHelpers.IsPrimitiveDisplay(m.ReturnTypeDisplay)) primitiveState++;
            }

            if (totalState >= SmellThresholds.PrimitiveObsessionMinCount && primitiveState == totalState)
            {
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, type.Ref, Member: null,
                    $"{totalState} primitive fields/properties — consider a value object");
            }
        }
    }
}
