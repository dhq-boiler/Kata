using Kata.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kata.Roslyn.Analysis.Detectors;

// A type whose instance methods are mostly one-line delegations to a held reference
// (`return _x.M(args);` or `_x.M(args);`). Fowler: skip the middle man.
internal sealed class MiddleManDetector : IRoslynSmellDetector
{
    public SmellCategory Category => SmellCategory.MiddleMan;

    private const double DelegationRatio = 0.5;
    private const int MinMethodsToConsider = 3;

    public IEnumerable<CodeSmell> Detect(RoslynSmellContext context, CancellationToken ct)
    {
        foreach (var (typeRef, sym, _) in context.HandwrittenTypes())
        {
            ct.ThrowIfCancellationRequested();

            var total = 0;
            var delegated = 0;
            foreach (var method in DetectorHelpers.HandwrittenMethods(sym))
            {
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                    or MethodKind.Destructor) continue;
                total++;
                if (IsDelegation(method, ct)) delegated++;
            }

            if (total < MinMethodsToConsider) continue;
            if ((double)delegated / total < DelegationRatio) continue;

            yield return new CodeSmell(
                Category, SmellSeverity.Info, typeRef, Member: null,
                $"{delegated}/{total} methods just delegate — skip the middle man");
        }
    }

    private static bool IsDelegation(IMethodSymbol method, CancellationToken ct)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var decl = syntaxRef.GetSyntax(ct);
            ExpressionSyntax? expr = null;

            if (decl is MethodDeclarationSyntax md)
            {
                if (md.ExpressionBody is { } eb) expr = eb.Expression;
                else if (md.Body is { Statements: { Count: 1 } stmts })
                {
                    expr = stmts[0] switch
                    {
                        ReturnStatementSyntax r => r.Expression,
                        ExpressionStatementSyntax es => es.Expression,
                        _ => null,
                    };
                }
            }
            if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }
                && ma.Expression is not ThisExpressionSyntax)
            {
                return true;
            }
        }
        return false;
    }
}
