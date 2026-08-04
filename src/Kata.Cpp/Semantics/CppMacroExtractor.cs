using System.Text;

namespace Kata.Cpp.Semantics;

/// <summary>
/// Line-oriented scanner for <c>#define</c> directives. Extracts object-like
/// (<c>#define NAME value</c>) and function-like (<c>#define NAME(a,b) body</c>)
/// macros, joining backslash-continued lines. Bare macros with no replacement
/// text (typical include-guard sentinels like <c>_FOO_H_</c>) are skipped —
/// they would flood the diagram without adding refactoring signal.
/// </summary>
public static class CppMacroExtractor
{
    public static IReadOnlyList<CppMacroSymbol> Extract(string filePath, string source)
    {
        var results = new List<CppMacroSymbol>();
        // Same file often defines a macro twice under `#if X ... #else ... #endif` (only
        // one branch is compiled, but this scanner sees both). Keep the first occurrence
        // per (Name, IsFunctionLike, ParamCount) triple — an overloading-shaped redefinition
        // (rare) still lands, but plain #else duplicates collapse to a single diagram row.
        var seen = new HashSet<(string Name, bool IsFunctionLike, int ParamCount)>();
        int i = 0;
        int line = 1;

        while (i < source.Length)
        {
            // Skip leading whitespace (spaces / tabs) on this logical line.
            while (i < source.Length && (source[i] == ' ' || source[i] == '\t')) i++;

            if (i >= source.Length) break;

            if (source[i] == '#')
            {
                i++;
                while (i < source.Length && (source[i] == ' ' || source[i] == '\t')) i++;

                if (i + 6 <= source.Length
                    && source[i] == 'd' && source[i + 1] == 'e' && source[i + 2] == 'f'
                    && source[i + 3] == 'i' && source[i + 4] == 'n' && source[i + 5] == 'e'
                    && (i + 6 == source.Length || IsIdentifierBreak(source[i + 6])))
                {
                    i += 6;
                    while (i < source.Length && (source[i] == ' ' || source[i] == '\t')) i++;

                    // Macro name.
                    int nameStart = i;
                    while (i < source.Length && IsIdentifierChar(source[i])) i++;
                    int nameEnd = i;

                    if (nameEnd > nameStart)
                    {
                        var name = source.Substring(nameStart, nameEnd - nameStart);
                        var nameLine = line;

                        // Function-like macros need '(' IMMEDIATELY after the name (no whitespace).
                        var parameters = new List<string>();
                        bool isFunctionLike = false;
                        if (i < source.Length && source[i] == '(')
                        {
                            isFunctionLike = true;
                            i++;
                            int paramStart = i;
                            int depth = 1;
                            while (i < source.Length && depth > 0)
                            {
                                var c = source[i];
                                if (c == '(') depth++;
                                else if (c == ')')
                                {
                                    depth--;
                                    if (depth == 0)
                                    {
                                        AddParameter(source, paramStart, i, parameters);
                                        i++;
                                        break;
                                    }
                                }
                                else if (c == ',' && depth == 1)
                                {
                                    AddParameter(source, paramStart, i, parameters);
                                    paramStart = i + 1;
                                }
                                else if (c == '\n')
                                {
                                    // '(' left dangling — give up on this macro.
                                    line++;
                                    isFunctionLike = false;
                                    parameters.Clear();
                                    break;
                                }
                                i++;
                            }
                        }

                        // Skip whitespace between params and replacement.
                        while (i < source.Length && (source[i] == ' ' || source[i] == '\t')) i++;

                        // Replacement text: everything to end of line, with backslash-continuation.
                        var replacement = new StringBuilder();
                        while (i < source.Length)
                        {
                            var c = source[i];
                            if (c == '\\')
                            {
                                // Line continuation: `\` at end of line (optionally followed by whitespace + CR/LF).
                                int probe = i + 1;
                                while (probe < source.Length && (source[probe] == ' ' || source[probe] == '\t')) probe++;
                                if (probe < source.Length && (source[probe] == '\r' || source[probe] == '\n'))
                                {
                                    i = probe;
                                    if (i < source.Length && source[i] == '\r') i++;
                                    if (i < source.Length && source[i] == '\n') { i++; line++; }
                                    if (replacement.Length > 0 && replacement[^1] != ' ') replacement.Append(' ');
                                    continue;
                                }
                                replacement.Append(c);
                                i++;
                                continue;
                            }
                            if (c == '\n' || c == '\r') break;
                            replacement.Append(c);
                            i++;
                        }

                        var replacementText = replacement.ToString().Trim();

                        // Skip bare sentinels (include guards, feature-detection stubs) —
                        // no value AND not function-like. These would flood the diagram
                        // with noise (`_FILE_H_`, `WIN32`, etc.) with no refactoring signal.
                        if (isFunctionLike || replacementText.Length > 0)
                        {
                            if (seen.Add((name, isFunctionLike, parameters.Count)))
                            {
                                var site = new CppDeclarationSite(
                                    filePath,
                                    new CppSpan(nameStart, nameEnd - nameStart, nameLine));
                                results.Add(new CppMacroSymbol(
                                    Name: name,
                                    IsFunctionLike: isFunctionLike,
                                    ReplacementText: replacementText,
                                    Parameters: parameters,
                                    Site: site));
                            }
                        }
                    }
                }
            }

            // Advance to end of current line (skipping remainder of any directive we handled).
            while (i < source.Length && source[i] != '\n') i++;
            if (i < source.Length && source[i] == '\n') { i++; line++; }
        }

        return results;
    }

    private static void AddParameter(string source, int start, int end, List<string> sink)
    {
        var raw = source.Substring(start, end - start).Trim();
        if (raw.Length > 0) sink.Add(raw);
    }

    private static bool IsIdentifierChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    private static bool IsIdentifierBreak(char c) => !IsIdentifierChar(c);
}
