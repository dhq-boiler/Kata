using Kata.Core.Model;
using Kata.Cpp.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kata.Roslyn.HybridResolution;

/// <summary>
/// Falls back to a <see cref="CppCompilation"/> when Roslyn cannot resolve a
/// C# member access because the receiver's declared type lives in a
/// C++/CLI project that MSBuildWorkspace didn't wire as a C# reference.
/// </summary>
public sealed class HybridSymbolResolver
{
    private const int MaxInferenceDepth = 4;

    private readonly CppCompilation _cpp;

    public HybridSymbolResolver(CppCompilation cpp)
    {
        _cpp = cpp;
    }

    /// <summary>
    /// Try to resolve a bare type-name identifier (e.g. the <c>ConnectionManager</c>
    /// in <c>ConnectionManager _mgr;</c> or <c>new ConnectionManager()</c>) whose
    /// declared type comes from the Cpp compilation. Returns a HybridResolveResult
    /// whose Member is a "landing" member (constructor if any, otherwise the first
    /// declared member) so the caller's existing (TypeRef, MemberRef) navigation
    /// contract carries us to the .h declaration region.
    /// </summary>
    public HybridResolveResult? TryResolveTypeName(SimpleNameSyntax name)
    {
        var typeName = name.Identifier.Text;
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        var info = _cpp.ResolveType(typeName);
        var type = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (type is null)
        {
            return null;
        }

        var landing = ChooseLandingMember(type);
        if (landing is null)
        {
            return null;
        }
        return new HybridResolveResult(type, landing, PreferTypeSite: true);
    }

    private static CppMemberSymbol? ChooseLandingMember(CppTypeSymbol type)
    {
        return type.Members.FirstOrDefault(m => m.Kind == MemberKind.Constructor)
            ?? type.Members.FirstOrDefault();
    }

    /// <summary>
    /// Resolve a target-typed <c>new()</c> / <c>new(args)</c> expression. Walks up
    /// to the enclosing variable / field / property declaration to recover the
    /// declared type name, then lands on the constructor whose arity matches the
    /// argument list (or the first constructor if none matches by arity).
    /// </summary>
    public HybridResolveResult? TryResolveImplicitObjectCreation(ImplicitObjectCreationExpressionSyntax node)
    {
        var typeName = FindEnclosingDeclaredTypeName(node);
        if (typeName is null)
        {
            return null;
        }

        var info = _cpp.ResolveType(typeName);
        var type = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (type is null)
        {
            return null;
        }

        var arity = node.ArgumentList?.Arguments.Count ?? 0;
        var landing = type.Members
                .FirstOrDefault(m => m.Kind == MemberKind.Constructor
                                  && m.Parameters.Count == arity)
            ?? type.Members.FirstOrDefault(m => m.Kind == MemberKind.Constructor)
            ?? type.Members.FirstOrDefault();
        if (landing is null)
        {
            return null;
        }
        return new HybridResolveResult(type, landing, PreferTypeSite: true);
    }

    private static string? FindEnclosingDeclaredTypeName(SyntaxNode? start)
    {
        for (var node = start?.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case VariableDeclarationSyntax vd:
                    return TypeSyntaxToName(vd.Type);
                case PropertyDeclarationSyntax pd:
                    return TypeSyntaxToName(pd.Type);
                case ParameterSyntax ps when ps.Type is not null:
                    return TypeSyntaxToName(ps.Type);
                case ArgumentSyntax:
                case AttributeSyntax:
                case MemberDeclarationSyntax:
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Try to resolve a <see cref="MemberAccessExpressionSyntax"/> whose receiver
    /// has an unresolved type. Returns null when we can't recover the type name
    /// or the Cpp compilation doesn't know the type.
    /// </summary>
    public HybridResolveResult? TryResolveMemberAccess(
        MemberAccessExpressionSyntax node,
        SemanticModel semantic)
    {
        var receiverType = InferCppType(node.Expression, semantic, depth: 0);
        if (receiverType is null)
        {
            return null;
        }

        var memberName = node.Name.Identifier.Text;
        var arity = TryInferInvocationArity(node);
        var memberInfo = _cpp.ResolveMember(receiverType, memberName, arity);
        var member = memberInfo.Symbol ?? memberInfo.CandidateSymbols.FirstOrDefault();
        if (member is null)
        {
            return null;
        }

        return new HybridResolveResult(receiverType, member);
    }

    /// <summary>
    /// Try to infer the <see cref="CppTypeSymbol"/> that an expression evaluates to.
    /// Handles direct identifier lookups (via declared type syntax), <c>var</c>-typed
    /// locals (by recursing into the initializer expression), invocation chains
    /// (by resolving the receiver, then looking up the Cpp member and normalising
    /// its return-type name), and simple <c>new X()</c> expressions.
    /// </summary>
    private CppTypeSymbol? InferCppType(ExpressionSyntax? expr, SemanticModel semantic, int depth)
    {
        if (expr is null || depth >= MaxInferenceDepth)
        {
            return null;
        }

        // Fast path: Roslyn already knows a non-error type. Map that type's name into Cpp.
        var typeInfo = semantic.GetTypeInfo(expr);
        if (typeInfo.Type is INamedTypeSymbol nt
            && nt.TypeKind != Microsoft.CodeAnalysis.TypeKind.Error
            && !string.IsNullOrEmpty(nt.Name))
        {
            var directInfo = _cpp.ResolveType(nt.Name);
            var direct = directInfo.Symbol ?? directInfo.CandidateSymbols.FirstOrDefault();
            if (direct is not null)
            {
                return direct;
            }
        }

        return expr switch
        {
            ParenthesizedExpressionSyntax paren
                => InferCppType(paren.Expression, semantic, depth),
            IdentifierNameSyntax id
                => InferCppTypeFromIdentifier(id, semantic, depth + 1),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } invoc
                => InferCppTypeFromInvocation(invoc, ma, semantic, depth + 1),
            ObjectCreationExpressionSyntax { Type: SimpleNameSyntax simple }
                => ResolveCppTypeByName(simple.Identifier.Text),
            ObjectCreationExpressionSyntax { Type: QualifiedNameSyntax qn }
                => ResolveCppTypeByName(qn.ToString()),
            _ => null,
        };
    }

    private CppTypeSymbol? ResolveCppTypeByName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }
        var info = _cpp.ResolveType(typeName);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private CppTypeSymbol? InferCppTypeFromIdentifier(
        IdentifierNameSyntax id,
        SemanticModel semantic,
        int depth)
    {
        var symbol = semantic.GetSymbolInfo(id).Symbol
                     ?? semantic.GetSymbolInfo(id).CandidateSymbols.FirstOrDefault();
        if (symbol is null)
        {
            return null;
        }

        var direct = TypeNameFromDeclaration(symbol);
        if (direct is not null)
        {
            var directLookup = ResolveCppTypeByName(direct);
            if (directLookup is not null)
            {
                return directLookup;
            }
        }

        // var / missing type / not-in-Cpp — recurse through the initializer expression.
        var initializer = GetInitializerExpression(symbol);
        if (initializer is not null)
        {
            return InferCppType(initializer, semantic, depth);
        }
        return null;
    }

    private CppTypeSymbol? InferCppTypeFromInvocation(
        InvocationExpressionSyntax invoc,
        MemberAccessExpressionSyntax ma,
        SemanticModel semantic,
        int depth)
    {
        var receiverType = InferCppType(ma.Expression, semantic, depth);
        if (receiverType is null)
        {
            return null;
        }
        var arity = invoc.ArgumentList.Arguments.Count;
        var memberInfo = _cpp.ResolveMember(receiverType, ma.Name.Identifier.Text, arity);
        var member = memberInfo.Symbol ?? memberInfo.CandidateSymbols.FirstOrDefault();
        if (member is null)
        {
            return null;
        }

        var returnTypeName = SymbolKeyFormatter.NormalizeCppTypeName(member.ReturnTypeDisplay);
        return ResolveCppTypeByName(returnTypeName);
    }

    private static string? TypeNameFromDeclaration(ISymbol symbol)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            return null;
        }

        var node = reference.GetSyntax();
        var typeSyntax = node switch
        {
            VariableDeclaratorSyntax v when v.Parent is VariableDeclarationSyntax d => d.Type,
            ParameterSyntax p => p.Type,
            PropertyDeclarationSyntax p => p.Type,
            _ => null,
        };

        return typeSyntax is null ? null : TypeSyntaxToName(typeSyntax);
    }

    private static ExpressionSyntax? GetInitializerExpression(ISymbol symbol)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
        {
            return null;
        }
        var node = reference.GetSyntax();
        return node switch
        {
            VariableDeclaratorSyntax v => v.Initializer?.Value,
            PropertyDeclarationSyntax p => p.Initializer?.Value,
            _ => null,
        };
    }

    private static string? TypeSyntaxToName(TypeSyntax type) => type switch
    {
        // "var" is a contextual keyword in C# — treat it as "we don't syntactically know the type"
        // so the caller falls back to initializer-based inference.
        IdentifierNameSyntax id when id.Identifier.Text == "var" => null,
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax q => q.ToString(),
        GenericNameSyntax g => g.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        _ => null,
    };

    private static int? TryInferInvocationArity(MemberAccessExpressionSyntax node)
    {
        if (node.Parent is InvocationExpressionSyntax invocation)
        {
            return invocation.ArgumentList.Arguments.Count;
        }
        return null;
    }
}

public sealed record HybridResolveResult(
    CppTypeSymbol Type,
    CppMemberSymbol Member,
    bool PreferTypeSite = false);
