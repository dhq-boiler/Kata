using Kata.Core.Model;

namespace Kata.Cpp.Semantics;

public sealed class CppTypeSymbol
{
    private CppTypeSymbol[] _baseTypes = Array.Empty<CppTypeSymbol>();
    private CppMemberSymbol[] _members = Array.Empty<CppMemberSymbol>();

    public string FullyQualifiedName { get; }
    public string Name { get; }
    public string NamespaceFullName { get; }
    public TypeKind Kind { get; }
    public CppDeclarationSite DeclarationSite { get; }
    public bool IsAbstract { get; }
    public bool IsSealed { get; }
    public IReadOnlyList<CppTypeSymbol> BaseTypes => _baseTypes;
    public IReadOnlyList<CppMemberSymbol> Members => _members;

    internal CppTypeSymbol(
        string fullyQualifiedName,
        string name,
        string namespaceFullName,
        TypeKind kind,
        CppDeclarationSite declarationSite,
        bool isAbstract = false,
        bool isSealed = false)
    {
        FullyQualifiedName = fullyQualifiedName;
        Name = name;
        NamespaceFullName = namespaceFullName;
        Kind = kind;
        DeclarationSite = declarationSite;
        IsAbstract = isAbstract;
        IsSealed = isSealed;
    }

    internal void FinalizeMembers(IEnumerable<CppMemberSymbol> members)
        => _members = members.ToArray();

    internal void FinalizeBaseTypes(IEnumerable<CppTypeSymbol> baseTypes)
        => _baseTypes = baseTypes.ToArray();
}
