using Kata.Core.Analysis;
using Microsoft.CodeAnalysis;

namespace Kata.Roslyn.Analysis.Detectors;

// Two types that lean heavily on each other's internal members — bidirectional coupling.
// Heuristic: count MemberAccess whose receiver resolves to a foreign type; if two types A and
// B each mention >= N of each other's internal (private/internal) members, flag both.
internal sealed class InsiderTradingDetector : IRoslynSmellDetector
{
    public SmellCategory Category => SmellCategory.InsiderTrading;

    private const int MinCrossRefs = 4;

    public IEnumerable<CodeSmell> Detect(RoslynSmellContext context, CancellationToken ct)
    {
        // (source, target) -> count of source's syntactic mentions of internal-ish members of target
        var cross = new Dictionary<(INamedTypeSymbol Src, INamedTypeSymbol Dst), int>(
            new PairComparer());

        foreach (var (_, sym, _) in context.HandwrittenTypes())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var syntaxRef in sym.DeclaringSyntaxReferences)
            {
                var tree = syntaxRef.SyntaxTree;
                var model = FindModel(context, tree);
                if (model is null) continue;

                var decl = syntaxRef.GetSyntax(ct);
                foreach (var node in decl.DescendantNodes())
                {
                    if (node is not Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma)
                        continue;
                    var target = model.GetSymbolInfo(ma, ct).Symbol;
                    if (target?.ContainingType is not { } other) continue;
                    if (SymbolEqualityComparer.Default.Equals(other, sym)) continue;
                    if (target.DeclaredAccessibility is not (Accessibility.Private
                        or Accessibility.Internal or Accessibility.ProtectedAndInternal)) continue;
                    var key = (sym, other);
                    cross[key] = cross.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
        }

        var reported = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var kv in cross)
        {
            if (kv.Value < MinCrossRefs) continue;
            var reverse = (kv.Key.Dst, kv.Key.Src);
            if (!cross.TryGetValue(reverse, out var rev) || rev < MinCrossRefs) continue;
            foreach (var t in new[] { kv.Key.Src, kv.Key.Dst })
            {
                if (!reported.Add(t)) continue;
                yield return new CodeSmell(
                    Category, SmellSeverity.Info,
                    Kata.Roslyn.ModelBuilding.RoslynToModelMapper.ToTypeRef(t),
                    Member: null,
                    $"heavy bidirectional access with sibling — leaking internals");
            }
        }
    }

    private static SemanticModel? FindModel(RoslynSmellContext ctx, SyntaxTree tree)
    {
        foreach (var comp in ctx.Compilations)
            if (comp.SyntaxTrees.Contains(tree)) return comp.GetSemanticModel(tree);
        return null;
    }

    private sealed class PairComparer : IEqualityComparer<(INamedTypeSymbol Src, INamedTypeSymbol Dst)>
    {
        public bool Equals((INamedTypeSymbol Src, INamedTypeSymbol Dst) x, (INamedTypeSymbol Src, INamedTypeSymbol Dst) y)
            => SymbolEqualityComparer.Default.Equals(x.Src, y.Src)
               && SymbolEqualityComparer.Default.Equals(x.Dst, y.Dst);
        public int GetHashCode((INamedTypeSymbol Src, INamedTypeSymbol Dst) obj)
            => HashCode.Combine(
                SymbolEqualityComparer.Default.GetHashCode(obj.Src),
                SymbolEqualityComparer.Default.GetHashCode(obj.Dst));
    }
}
