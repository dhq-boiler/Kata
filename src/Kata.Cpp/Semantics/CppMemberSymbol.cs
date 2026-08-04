using Kata.Core.Model;

namespace Kata.Cpp.Semantics;

public sealed class CppMemberSymbol
{
    private CppDeclarationSite? _implementationSite;

    public CppTypeSymbol ContainingType { get; }
    public string Name { get; }
    public MemberKind Kind { get; }
    public string Signature { get; }
    public CppDeclarationSite DeclarationSite { get; }
    public bool IsStatic { get; }
    public IReadOnlyList<CppParameter> Parameters { get; }

    /// <summary>
    /// Raw return-type text as parsed from the header (e.g. <c>ConnectionHandle^</c>).
    /// Empty for fields / properties / events / constructors.
    /// </summary>
    public string ReturnTypeDisplay { get; }

    /// <summary>
    /// Location of the out-of-class body in a .cpp source, when discovered.
    /// Null for header-only declarations or when the .cpp couldn't be parsed.
    /// </summary>
    public CppDeclarationSite? ImplementationSite => _implementationSite;

    internal CppMemberSymbol(
        CppTypeSymbol containingType,
        string name,
        MemberKind kind,
        string signature,
        CppDeclarationSite declarationSite,
        bool isStatic,
        IReadOnlyList<CppParameter> parameters,
        string returnTypeDisplay = "")
    {
        ContainingType = containingType;
        Name = name;
        Kind = kind;
        Signature = signature;
        DeclarationSite = declarationSite;
        IsStatic = isStatic;
        Parameters = parameters;
        ReturnTypeDisplay = returnTypeDisplay;
    }

    internal void AttachImplementationSite(CppDeclarationSite site) => _implementationSite = site;
}
