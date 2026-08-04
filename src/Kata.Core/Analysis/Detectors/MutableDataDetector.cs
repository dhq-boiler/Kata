using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// public instance field (非 const 非 readonly) と public settable (非 init) property。
// record は言語側でイミュータビリティを既に表現しているので除外。
public sealed class MutableDataDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.MutableData;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            if (type.Kind == TypeKind.Record) continue;
            if (type.Kind is not TypeKind.Class and not TypeKind.Struct) continue;

            foreach (var m in type.Members)
            {
                if (m.IsStatic) continue;
                if (m.Accessibility != MemberAccessibility.Public) continue;

                switch (m.Kind)
                {
                    case MemberKind.Field when !m.IsReadOnly:
                        yield return new CodeSmell(
                            Category, SmellSeverity.Info, type.Ref, m.Ref,
                            "public mutable field — consider encapsulation");
                        break;
                    case MemberKind.Property when !m.IsReadOnly:
                        yield return new CodeSmell(
                            Category, SmellSeverity.Info, type.Ref, m.Ref,
                            "public settable property — consider init/readonly");
                        break;
                }
            }
        }
    }
}
