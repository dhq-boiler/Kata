using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// >= N (ClumpSize) 個並びの連続するパラメータ名タプルが、複数メソッドで再登場するなら
// それは埋もれたクラス。
public sealed class DataClumpsDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.DataClumps;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        var clumpsToMethods = new Dictionary<string, List<(TypeRef TypeRef, MemberRef MethodRef)>>(StringComparer.Ordinal);

        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in SmellDetectorHelpers.Methods(type))
            {
                var pcount = m.Parameters.Count;
                if (pcount < SmellThresholds.DataClumpsSize) continue;
                var names = m.Parameters.Select(p => p.Name).ToArray();
                for (var start = 0; start + SmellThresholds.DataClumpsSize <= names.Length; start++)
                {
                    var key = string.Join(",", names, start, SmellThresholds.DataClumpsSize);
                    if (!clumpsToMethods.TryGetValue(key, out var list))
                        clumpsToMethods[key] = list = new();
                    list.Add((type.Ref, m.Ref));
                }
            }
        }

        var reported = new HashSet<MemberRef>();
        foreach (var kv in clumpsToMethods)
        {
            if (kv.Value.Count < 2) continue;
            foreach (var (typeRef, methodRef) in kv.Value)
            {
                if (!reported.Add(methodRef)) continue;
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, typeRef, methodRef,
                    $"shares parameter tuple ({kv.Key}) with other methods — extract parameter object");
            }
        }
    }
}
