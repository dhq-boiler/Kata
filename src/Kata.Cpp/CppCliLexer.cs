namespace Kata.Cpp;

public enum CppTokenKind
{
    Identifier,
    Punctuation,
    EndOfFile,
}

public readonly record struct CppToken(
    CppTokenKind Kind,
    string Text,
    int Position = 0,
    int Length = 0,
    int Line = 0);

public static class CppCliLexer
{
    public static IReadOnlyList<CppToken> Tokenize(string source)
    {
        var tokens = new List<CppToken>();
        var i = 0;
        var line = 1;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            if (char.IsWhiteSpace(c))
            {
                if (c == '\n') line++;
                i++;
                continue;
            }

            // Line comment
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n') i++;
                continue; // '\n' itself will be handled by the whitespace branch above
            }

            // Block comment
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') line++;
                    i++;
                }
                if (i + 1 < n) i += 2;
                else i = n;
                continue;
            }

            // Preprocessor directive — skip to end of (possibly line-continued) line
            if (c == '#')
            {
                i = SkipPreprocessorLine(source, i, ref line);
                continue;
            }

            // Raw string literal: R"delim( ... )delim"
            if (c == 'R' && i + 1 < n && source[i + 1] == '"')
            {
                i = SkipRawString(source, i + 2, ref line);
                continue;
            }

            // String literal
            if (c == '"')
            {
                i = SkipString(source, i + 1, '"', ref line);
                continue;
            }

            // Char literal
            if (c == '\'')
            {
                i = SkipString(source, i + 1, '\'', ref line);
                continue;
            }

            // Identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                var startLine = line;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                var len = i - start;
                tokens.Add(new CppToken(
                    CppTokenKind.Identifier,
                    source.Substring(start, len),
                    Position: start,
                    Length: len,
                    Line: startLine));
                continue;
            }

            // Number literal — skip as opaque punctuation (rare in decl scope)
            if (char.IsDigit(c))
            {
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '.' || source[i] == '\'')) i++;
                continue;
            }

            // Two-char punctuation: ::
            if (c == ':' && i + 1 < n && source[i + 1] == ':')
            {
                tokens.Add(new CppToken(
                    CppTokenKind.Punctuation, "::",
                    Position: i, Length: 2, Line: line));
                i += 2;
                continue;
            }

            // Two-char punctuation: -> (pointer-to-member access, common in C++/CLI bodies)
            if (c == '-' && i + 1 < n && source[i + 1] == '>')
            {
                tokens.Add(new CppToken(
                    CppTokenKind.Punctuation, "->",
                    Position: i, Length: 2, Line: line));
                i += 2;
                continue;
            }

            // Single-char punctuation
            tokens.Add(new CppToken(
                CppTokenKind.Punctuation, c.ToString(),
                Position: i, Length: 1, Line: line));
            i++;
        }

        tokens.Add(new CppToken(
            CppTokenKind.EndOfFile, string.Empty,
            Position: n, Length: 0, Line: line));
        return tokens;
    }

    private static int SkipPreprocessorLine(string src, int i, ref int line)
    {
        var n = src.Length;
        while (i < n)
        {
            if (src[i] == '\n')
            {
                line++;
                return i + 1;
            }
            if (src[i] == '\\' && i + 1 < n)
            {
                // line continuation — skip \r?\n
                var j = i + 1;
                while (j < n && (src[j] == '\r' || src[j] == ' ' || src[j] == '\t')) j++;
                if (j < n && src[j] == '\n')
                {
                    line++;
                    i = j + 1;
                    continue;
                }
            }
            i++;
        }
        return n;
    }

    private static int SkipString(string src, int i, char terminator, ref int line)
    {
        var n = src.Length;
        while (i < n)
        {
            if (src[i] == '\\' && i + 1 < n)
            {
                if (src[i + 1] == '\n') line++;
                i += 2;
                continue;
            }
            if (src[i] == terminator) return i + 1;
            if (src[i] == '\n') line++;
            i++;
        }
        return n;
    }

    private static int SkipRawString(string src, int i, ref int line)
    {
        var n = src.Length;
        // Read delimiter up to '('
        var delimStart = i;
        while (i < n && src[i] != '(') i++;
        var delim = src.Substring(delimStart, i - delimStart);
        if (i >= n) return n;
        i++; // past '('

        var closing = ")" + delim + "\"";
        var closeIdx = src.IndexOf(closing, i, StringComparison.Ordinal);
        if (closeIdx < 0)
        {
            // Count remaining newlines to keep line tracking accurate.
            for (var k = i; k < n; k++)
            {
                if (src[k] == '\n') line++;
            }
            return n;
        }
        for (var k = i; k < closeIdx; k++)
        {
            if (src[k] == '\n') line++;
        }
        return closeIdx + closing.Length;
    }
}
