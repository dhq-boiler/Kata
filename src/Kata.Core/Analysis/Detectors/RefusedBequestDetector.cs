using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// 継承しているのに親のメンバーを 1 つも参照していない subclass。
// heuristic: base の non-private メンバー名を集めて、subclass の型全体 source に
// どれか 1 つでも登場しなければ Info。
public sealed class RefusedBequestDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.RefusedBequest;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            if (type.BaseTypes.Count == 0) continue;

            var baseRef = type.BaseTypes[0];
            if (!context.TryGetType(baseRef, out var baseType) || baseType is null) continue;

            var baseNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in baseType.Members)
            {
                if (m.IsStatic) continue;
                if (m.Accessibility is MemberAccessibility.Private) continue;
                baseNames.Add(m.Name);
            }
            if (baseNames.Count == 0) continue;

            var source = context.GetTypeText(type.Ref);
            if (string.IsNullOrEmpty(source)) continue;

            var used = false;
            foreach (var name in baseNames)
            {
                if (source.Contains(name, StringComparison.Ordinal)) { used = true; break; }
            }
            if (used) continue;

            yield return new CodeSmell(
                Category, SmellSeverity.Info, type.Ref, Member: null,
                $"never uses {baseNames.Count} inherited member(s) from {baseType.Name}");
        }
    }
}
