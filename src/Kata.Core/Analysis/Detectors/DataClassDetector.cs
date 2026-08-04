using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// 状態は持つが振る舞いのないクラス。record / static / abstract は除外。
public sealed class DataClassDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.DataClass;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();

            if (type.Kind is not TypeKind.Class and not TypeKind.Struct) continue;
            if (type.IsStatic || type.IsAbstract) continue;

            var nonCtorMethods = 0;
            var stateSlots = 0;
            foreach (var m in type.Members)
            {
                switch (m.Kind)
                {
                    case MemberKind.Method:
                        nonCtorMethods++;
                        break;
                    case MemberKind.Field:
                    case MemberKind.Property:
                        stateSlots++;
                        break;
                }
            }

            if (nonCtorMethods == 0 && stateSlots >= 1)
            {
                yield return new CodeSmell(
                    Category, SmellSeverity.Warning, type.Ref, Member: null,
                    $"data-only class ({stateSlots} fields/properties, no methods)");
            }
        }
    }
}
