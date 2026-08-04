using Kata.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kata.Roslyn.Analysis.Detectors;

// A method more interested in another class than its own: counts MemberAccess whose receiver
// resolves to a symbol from a foreign type vs the enclosing type. Threshold: foreign accesses
// >= 5 and > self accesses. Uses SemanticModel; skips methods whose tree has no model.
internal sealed class FeatureEnvyDetector : IRoslynSmellDetector
{
    public SmellCategory Category => SmellCategory.FeatureEnvy;

    private const int MinForeignAccesses = 5;

    public IEnumerable<CodeSmell> Detect(RoslynSmellContext context, CancellationToken ct)
    {
        foreach (var (typeRef, sym, _) in context.HandwrittenTypes())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var method in DetectorHelpers.HandwrittenMethods(sym))
            {
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                    or MethodKind.Destructor) continue;

                var self = 0;
                var foreign = 0;

                foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                {
                    var decl = syntaxRef.GetSyntax(ct);
                    var body = DetectorHelpers.GetMethodBody(decl);
                    if (body is null) continue;

                    var tree = decl.SyntaxTree;
                    var model = FindModel(context, tree);
                    if (model is null) continue;

                    foreach (var access in body.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
                    {
                        var symbolInfo = model.GetSymbolInfo(access, ct).Symbol;
                        if (symbolInfo is null) continue;
                        var container = symbolInfo.ContainingType;
                        if (container is null) continue;
                        if (SymbolEqualityComparer.Default.Equals(container, sym)) self++;
                        else if (container.ContainingAssembly is { } a
                                 && SymbolEqualityComparer.Default.Equals(a, sym.ContainingAssembly))
                        {
                            foreign++;
                        }
                    }
                }

                if (foreign < MinForeignAccesses) continue;
                if (foreign <= self) continue;

                yield return new CodeSmell(
                    Category, SmellSeverity.Info, typeRef,
                    DetectorHelpers.ToMemberRef(method),
                    $"{foreign} foreign vs {self} self accesses — move to the target type");
            }
        }
    }

    private static SemanticModel? FindModel(RoslynSmellContext ctx, SyntaxTree tree)
    {
        foreach (var comp in ctx.Compilations)
        {
            if (comp.SyntaxTrees.Contains(tree)) return comp.GetSemanticModel(tree);
        }
        return null;
    }
}
