using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// public static な mutable field / settable property を "global data" として警告する。
// Singleton パターン (public static のフィールドで型が自分自身) は Info で計上。
public sealed class GlobalDataDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.GlobalData;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in type.Members)
            {
                if (!m.IsStatic) continue;
                if (m.Accessibility != MemberAccessibility.Public) continue;

                switch (m.Kind)
                {
                    case MemberKind.Field when !m.IsReadOnly:
                        // Singleton 検出: 型自身と ReturnTypeDisplay がマッチ
                        if (LooksLikeOwnerType(m.ReturnTypeDisplay, type))
                        {
                            yield return new CodeSmell(
                                Category, SmellSeverity.Info, type.Ref, m.Ref,
                                "Singleton pattern — global data");
                        }
                        else
                        {
                            yield return new CodeSmell(
                                Category, SmellSeverity.Warning, type.Ref, m.Ref,
                                "public static mutable field — global data");
                        }
                        break;
                    case MemberKind.Property when !m.IsReadOnly:
                        yield return new CodeSmell(
                            Category, SmellSeverity.Warning, type.Ref, m.Ref,
                            "public static settable property — global data");
                        break;
                }
            }
        }
    }

    // ReturnTypeDisplay は "FQN" / "Name" / "Name^" (C++/CLI) のどれか。緩く一致判定。
    private static bool LooksLikeOwnerType(string returnTypeDisplay, TypeModel owner)
    {
        if (string.IsNullOrEmpty(returnTypeDisplay)) return false;
        var trimmed = returnTypeDisplay.TrimEnd('^').Trim();
        if (string.Equals(trimmed, owner.Ref.FullyQualifiedName, StringComparison.Ordinal)) return true;
        if (string.Equals(trimmed, owner.Name, StringComparison.Ordinal)) return true;
        return false;
    }
}
