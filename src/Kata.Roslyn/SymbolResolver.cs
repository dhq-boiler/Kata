using Kata.Core.Model;
using Kata.Roslyn.ModelBuilding;
using Microsoft.CodeAnalysis;

namespace Kata.Roslyn;

internal static class SymbolResolver
{
    public static async Task<ISymbol?> ResolveAsync(
        Solution solution,
        TypeRef typeRef,
        MemberRef? memberRef,
        CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var typeSymbol = FindType(compilation.Assembly.GlobalNamespace, typeRef.FullyQualifiedName);
            if (typeSymbol is null)
            {
                continue;
            }

            if (memberRef is null)
            {
                return typeSymbol;
            }

            var expectedSignature = memberRef.Value.Signature;
            foreach (var member in typeSymbol.GetMembers())
            {
                if (RoslynToModelMapper.ToMemberRef(member).Signature == expectedSignature)
                {
                    return member;
                }
            }
        }

        return null;
    }

    private static INamedTypeSymbol? FindType(INamespaceSymbol root, string fullyQualifiedName)
    {
        foreach (var type in EnumerateAllTypes(root))
        {
            if (RoslynToModelMapper.ToTypeRef(type).FullyQualifiedName == fullyQualifiedName)
            {
                return type;
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNested(type))
            {
                yield return nested;
            }
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in EnumerateAllTypes(childNs))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNested(nested))
            {
                yield return deeper;
            }
        }
    }
}
