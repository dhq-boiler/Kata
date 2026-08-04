using Kata.Core.Model;

namespace Kata.Core.Analysis.Detectors;

// interface / abstract class で、ロード済み solution 内に derivation / implementation が
// ゼロ (or 自分自身のみ) のもの。YAGNI 抽象化。
public sealed class SpeculativeGeneralityDetector : IUniversalSmellDetector
{
    public SmellCategory Category => SmellCategory.SpeculativeGenerality;

    public IEnumerable<CodeSmell> Detect(ISmellContext context, CancellationToken ct)
    {
        // 型 T が「使われている」= 他のどれかの TypeModel が BaseTypes / ImplementedInterfaces に T を含む
        var usedRefs = new HashSet<TypeRef>();
        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var bt in type.BaseTypes) usedRefs.Add(bt);
            foreach (var ii in type.ImplementedInterfaces) usedRefs.Add(ii);
        }

        foreach (var type in context.HandwrittenTypes)
        {
            ct.ThrowIfCancellationRequested();

            if (type.Kind == TypeKind.Interface)
            {
                if (!usedRefs.Contains(type.Ref))
                {
                    yield return new CodeSmell(
                        Category, SmellSeverity.Info, type.Ref, Member: null,
                        "interface has no implementations in the loaded solution");
                }
            }
            else if (type.Kind == TypeKind.Class && type.IsAbstract)
            {
                if (!usedRefs.Contains(type.Ref))
                {
                    yield return new CodeSmell(
                        Category, SmellSeverity.Info, type.Ref, Member: null,
                        "abstract class has no concrete subclasses");
                }
            }
        }
    }
}
