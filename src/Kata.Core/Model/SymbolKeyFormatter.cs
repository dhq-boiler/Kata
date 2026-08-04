namespace Kata.Core.Model;

/// <summary>
/// Canonical signature / type-name formatting shared by the Roslyn and Cpp semantic layers.
/// The Cpp side normalises raw parsed type strings to match what Roslyn's
/// SymbolDisplayFormat (UseSpecialTypes | EscapeKeywordIdentifiers) emits.
/// </summary>
public static class SymbolKeyFormatter
{
    public readonly record struct ParameterKey(string TypeDisplay, string Name);

    /// <summary>
    /// Normalise a C++/CLI raw type-name (as produced by <c>CppCliDeclParser</c>)
    /// to a form comparable with Roslyn's UseSpecialTypes display output.
    /// Example: "System::Action ^" → "System.Action", "String^" → "String".
    /// </summary>
    public static string NormalizeCppTypeName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();

        // Strip C++/CLI reference / pointer sigils.
        text = text.TrimEnd('^', '&', '*').Trim();

        // Collapse whitespace introduced by the parser between "::" separators.
        text = text.Replace(" :: ", "::")
                   .Replace(":: ", "::")
                   .Replace(" ::", "::");

        // Canonicalise namespace separator.
        text = text.Replace("::", ".");

        // Collapse any remaining redundant whitespace inside generic brackets etc.
        while (text.Contains("  "))
        {
            text = text.Replace("  ", " ");
        }

        return text;
    }

    /// <summary>
    /// Build the canonical method / constructor signature.
    /// Matches Roslyn's default MemberSignatureFormat shape:
    /// "&lt;return&gt; &lt;name&gt;(&lt;type&gt; &lt;name&gt;, ...)" — constructors omit the return type.
    /// </summary>
    public static string FormatMethodSignature(
        string returnTypeDisplay,
        string name,
        IReadOnlyList<ParameterKey> parameters)
    {
        var paramText = string.Join(
            ", ",
            parameters.Select(p => $"{NormalizeCppTypeName(p.TypeDisplay)} {p.Name}"));
        var returnPart = string.IsNullOrWhiteSpace(returnTypeDisplay)
            ? string.Empty
            : NormalizeCppTypeName(returnTypeDisplay) + " ";
        return $"{returnPart}{name}({paramText})";
    }

    /// <summary>
    /// Field / property / event signature: the member name is the canonical key.
    /// </summary>
    public static string FormatFieldSignature(string name) => name;
}
