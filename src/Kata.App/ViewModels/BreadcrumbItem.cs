using Kata.Core.Model;

namespace Kata.App.ViewModels;

public sealed record BreadcrumbItem(TypeRef OwnerType, MemberRef Member)
{
    // Kept in sync with CSharpLanguageAdapter.CppTypeSiteSignature — a MemberRef
    // whose signature equals this sentinel means the navigation target is the
    // type itself, not any of its members. Crumb hides the trailing ".{member}".
    private const string CppTypeSiteSignature = "<type>";

    // Marker for a breadcrumb that came from double-clicking a find-refs row.
    // The synthetic OwnerType is <ref:filename>; the Signature carries
    // "<display>:file:line" so Label can render "display (file:line)".
    private const string RefTypePrefix = "<ref:";

    public string Label => IsRefSite
        ? RefLabel()
        : IsTypeSite
            ? ShortTypeName(OwnerType)
            : $"{ShortTypeName(OwnerType)}.{ShortMemberName(Member.Signature)}";

    private bool IsTypeSite => Member.Signature == CppTypeSiteSignature;
    private bool IsRefSite => OwnerType.FullyQualifiedName.StartsWith(RefTypePrefix, System.StringComparison.Ordinal);

    // Signature format for a ref crumb: "<display> || file:line". Parse both.
    private string RefLabel()
    {
        var sig = Member.Signature;
        var sepIdx = sig.IndexOf(" || ", System.StringComparison.Ordinal);
        if (sepIdx < 0) return sig;
        var display = sig[..sepIdx];
        var suffix = sig[(sepIdx + 4)..];
        return $"{display} ({suffix})";
    }

    private static string ShortTypeName(TypeRef t)
    {
        var name = t.FullyQualifiedName;
        var lastDot = name.LastIndexOf('.');
        return lastDot < 0 ? name : name[(lastDot + 1)..];
    }

    private static string ShortMemberName(string signature)
    {
        var paren = signature.IndexOf('(');
        var beforeParen = paren < 0 ? signature : signature[..paren];
        var lastSpace = beforeParen.LastIndexOf(' ');
        var name = lastSpace < 0 ? beforeParen : beforeParen[(lastSpace + 1)..];
        return paren < 0 ? name : name + "()";
    }
}
