using Kata.Core.Model;
using Kata.Roslyn.ModelBuilding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kata.Roslyn.Analysis;

// Shared analysis primitives for the Roslyn detectors. Kept static + allocation-light — the
// detectors iterate every method in every type, so per-symbol work must stay cheap.
internal static class DetectorHelpers
{
    public static IEnumerable<(TypeRef Ref, INamedTypeSymbol Symbol, TypeModel Model)> HandwrittenTypes(
        this RoslynSmellContext ctx)
    {
        foreach (var kv in ctx.TypeModels)
        {
            if (ctx.TypeSymbols.TryGetValue(kv.Key, out var sym))
                yield return (kv.Key, sym, kv.Value);
        }
    }

    // Methods (incl. ctors, operators) that came from hand-written user source. Skips accessor
    // and event add/remove synthesised methods — those are surfaced by their owning property /
    // event instead. Mirrors RoslynToModelMapper.ShouldIncludeMember's stance on methods.
    public static IEnumerable<IMethodSymbol> HandwrittenMethods(INamedTypeSymbol type)
    {
        foreach (var m in type.GetMembers())
        {
            if (m is not IMethodSymbol method) continue;
            if (method.IsImplicitlyDeclared) continue;
            if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise) continue;
            if (method.DeclaringSyntaxReferences.Length == 0) continue;
            yield return method;
        }
    }

    // For methods / ctors / operators, the block body OR the expression body.
    public static SyntaxNode? GetMethodBody(SyntaxNode decl) => decl switch
    {
        MethodDeclarationSyntax md => (SyntaxNode?)md.Body ?? md.ExpressionBody,
        ConstructorDeclarationSyntax cd => (SyntaxNode?)cd.Body ?? cd.ExpressionBody,
        DestructorDeclarationSyntax dd => (SyntaxNode?)dd.Body ?? dd.ExpressionBody,
        OperatorDeclarationSyntax od => (SyntaxNode?)od.Body ?? od.ExpressionBody,
        ConversionOperatorDeclarationSyntax cvd => (SyntaxNode?)cvd.Body ?? cvd.ExpressionBody,
        LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
        _ => null,
    };

    public static int LineCount(SyntaxNode node)
    {
        var lineSpan = node.SyntaxTree.GetLineSpan(node.Span);
        return lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
    }

    public static MemberRef ToMemberRef(ISymbol symbol) => RoslynToModelMapper.ToMemberRef(symbol);

    // "Primitive-ish" in the Fowler sense: numeric / bool / char / string. Enums are borderline —
    // Fowler's primitive-obsession is about representing money / range / postcode as int/string
    // rather than a value type. Enums usually side-step the smell so exclude them.
    public static bool IsPrimitiveLike(ITypeSymbol t) => t.SpecialType switch
    {
        SpecialType.System_Boolean or
        SpecialType.System_Byte or
        SpecialType.System_SByte or
        SpecialType.System_Int16 or
        SpecialType.System_UInt16 or
        SpecialType.System_Int32 or
        SpecialType.System_UInt32 or
        SpecialType.System_Int64 or
        SpecialType.System_UInt64 or
        SpecialType.System_Single or
        SpecialType.System_Double or
        SpecialType.System_Decimal or
        SpecialType.System_Char or
        SpecialType.System_String => true,
        _ => false,
    };
}
