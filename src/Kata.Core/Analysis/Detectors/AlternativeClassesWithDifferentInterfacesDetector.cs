using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// 「異なる名前で同じ形」を持つ複数のクラス。public method の (return type × param types) を
// バケットキーにして、2 個以上入ったバケットの全型を Info で報告する。
public sealed class AlternativeClassesWithDifferentInterfacesDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.AlternativeClassesWithDifferentInterfaces;

    private const int MinMethods = 3;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        var buckets = new Dictionary<string, List<TypeRef>>(StringComparer.Ordinal);

        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            if (type.Kind is not TypeKind.Class and not TypeKind.Struct) continue;
            if (type.IsStatic || type.IsAbstract) continue;

            var sigs = new List<string>();
            foreach (var m in type.Members)
            {
                if (m.Kind != MemberKind.Method) continue;
                if (m.IsStatic) continue;
                if (m.Accessibility != MemberAccessibility.Public) continue;
                var paramShape = string.Join(",", m.Parameters.Select(p => p.TypeDisplay));
                sigs.Add($"{m.ReturnTypeDisplay}({paramShape})");
            }
            if (sigs.Count < MinMethods) continue;
            sigs.Sort(StringComparer.Ordinal);
            var key = string.Join("|", sigs);
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new();
            list.Add(type.Ref);
        }

        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;
            foreach (var typeRef in bucket)
            {
                yield return new CodeSmell(
                    Category, SmellSeverity.Info, typeRef, Member: null,
                    $"same public shape as {bucket.Count - 1} other type(s) — different method names");
            }
        }
    }
}
