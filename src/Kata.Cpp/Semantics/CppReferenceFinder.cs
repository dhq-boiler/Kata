using Kata.Cpp.Syntax;

namespace Kata.Cpp.Semantics;

/// <summary>
/// Scans a <see cref="CppCompilation"/>'s syntax and implementation trees for
/// occurrences of a target symbol. Heuristic (no full semantic model): matches
/// identifier tokens by simple name plus prev/next-token context. Accepts some
/// false positives when a member and an unrelated identifier collide by name.
/// </summary>
public static class CppReferenceFinder
{
    public static IReadOnlyList<CppReference> FindTypeReferences(
        CppCompilation compilation, CppTypeSymbol type)
    {
        var results = new List<CppReference>();
        var name = type.Name;
        var declPath = type.DeclarationSite.FilePath;
        var declStart = type.DeclarationSite.Span.Start;

        foreach (var tree in EnumerateAllTrees(compilation))
        {
            for (int i = 0; i < tree.Tokens.Count; i++)
            {
                var tok = tree.Tokens[i];
                if (tok.Kind != CppTokenKind.Identifier) continue;
                if (tok.Text != name) continue;

                var isDecl = IsSamePosition(tree.FilePath, tok.Position, declPath, declStart);
                if (isDecl)
                {
                    results.Add(BuildReference(tree, tok, CppReferenceKind.Declaration));
                    continue;
                }
                if (!LooksLikeTypeUse(tree.Tokens, i)) continue;
                results.Add(BuildReference(tree, tok, CppReferenceKind.TypeUse));
            }
        }
        return results;
    }

    public static IReadOnlyList<CppReference> FindMemberReferences(
        CppCompilation compilation, CppMemberSymbol member)
    {
        var results = new List<CppReference>();
        var name = member.Name;
        var isMethod = member.Kind == Core.Model.MemberKind.Method
                    || member.Kind == Core.Model.MemberKind.Constructor;
        var expectedArity = member.Parameters.Count;

        var declPaths = CollectDeclarationSites(member);

        foreach (var tree in EnumerateAllTrees(compilation))
        {
            for (int i = 0; i < tree.Tokens.Count; i++)
            {
                var tok = tree.Tokens[i];
                if (tok.Kind != CppTokenKind.Identifier) continue;
                if (tok.Text != name) continue;

                if (IsAnyDeclaration(tree.FilePath, tok.Position, declPaths))
                {
                    results.Add(BuildReference(tree, tok, CppReferenceKind.Declaration));
                    continue;
                }

                if (isMethod)
                {
                    if (!LooksLikeCallSite(tree.Tokens, i)) continue;
                    if (!CallArityMatches(tree.Tokens, i, expectedArity)) continue;
                    results.Add(BuildReference(tree, tok, CppReferenceKind.MethodCall));
                }
                else
                {
                    if (!LooksLikeMemberAccess(tree.Tokens, i)) continue;
                    results.Add(BuildReference(tree, tok, CppReferenceKind.MemberAccess));
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Count top-level commas (plus 1 for the first arg) between the <c>(</c>
    /// that follows the name token and its matching <c>)</c>. Returns true when
    /// the count matches <paramref name="expectedArity"/>. Empty <c>()</c> gives 0.
    /// </summary>
    private static bool CallArityMatches(IReadOnlyList<CppToken> tokens, int nameIdx, int expectedArity)
    {
        // Locate the opening paren.
        int openIdx = -1;
        for (int i = nameIdx + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == CppTokenKind.EndOfFile) continue;
            if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "(")
            {
                openIdx = i;
                break;
            }
            return false; // some other token; not a call after all
        }
        if (openIdx < 0) return false;

        // Walk to the matching close paren; count top-level commas.
        int depthParen = 0;
        int depthAngle = 0;
        int depthBrace = 0;
        int depthBracket = 0;
        int commas = 0;
        bool sawAnyToken = false;
        for (int i = openIdx; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.EndOfFile) continue;
            if (t.Kind == CppTokenKind.Punctuation)
            {
                switch (t.Text)
                {
                    case "(": depthParen++; break;
                    case ")":
                        depthParen--;
                        if (depthParen == 0)
                        {
                            int argCount = sawAnyToken ? commas + 1 : 0;
                            return argCount == expectedArity;
                        }
                        break;
                    case "<": depthAngle++; break;
                    case ">": if (depthAngle > 0) depthAngle--; break;
                    case "{": depthBrace++; break;
                    case "}": if (depthBrace > 0) depthBrace--; break;
                    case "[": depthBracket++; break;
                    case "]": if (depthBracket > 0) depthBracket--; break;
                    case ",":
                        if (depthParen == 1 && depthAngle == 0 && depthBrace == 0 && depthBracket == 0)
                        {
                            commas++;
                        }
                        break;
                }
            }
            if (i > openIdx && depthParen >= 1)
            {
                // any non-punct token inside parens marks "we have arguments"
                if (t.Kind == CppTokenKind.Identifier) sawAnyToken = true;
                else if (t.Kind == CppTokenKind.Punctuation && t.Text != "(" && t.Text != ")" && t.Text != ",") sawAnyToken = true;
            }
        }
        return false; // unmatched
    }

    private static IEnumerable<CppSyntaxTree> EnumerateAllTrees(CppCompilation compilation)
    {
        foreach (var t in compilation.SyntaxTrees) yield return t;
        foreach (var t in compilation.ImplementationTrees) yield return t;
    }

    private static bool IsSamePosition(string filePath, int position, string declPath, int declStart)
        => string.Equals(filePath, declPath, StringComparison.OrdinalIgnoreCase)
           && position == declStart;

    private static List<(string Path, int Start)> CollectDeclarationSites(CppMemberSymbol member)
    {
        var list = new List<(string, int)> { (member.DeclarationSite.FilePath, member.DeclarationSite.Span.Start) };
        if (member.ImplementationSite is { } impl)
        {
            list.Add((impl.FilePath, impl.Span.Start));
        }
        return list;
    }

    private static bool IsAnyDeclaration(string filePath, int position, List<(string Path, int Start)> declSites)
    {
        foreach (var (p, s) in declSites)
        {
            if (IsSamePosition(filePath, position, p, s)) return true;
        }
        return false;
    }

    private static bool LooksLikeTypeUse(IReadOnlyList<CppToken> tokens, int idx)
    {
        var prev = PrevMeaningful(tokens, idx);
        var next = NextMeaningful(tokens, idx);

        // Skip when the token is a MEMBER after a receiver operator — that's a member access, not a type use.
        if (prev is { Kind: CppTokenKind.Punctuation } p1
            && (p1.Text == "." || p1.Text == "->"))
        {
            return false;
        }

        // Common type-use contexts:
        //   new Foo(...)       — prev == "new"
        //   Foo^ x, Foo* x     — next == "^" | "*" | "&"
        //   Foo x              — next is an Identifier (declaration form)
        //   class C : Foo      — prev == ":"
        //   Foo::bar           — next == "::"
        //   template<Foo>      — prev == "<" and next == ">"
        //   (Foo)expr / (Foo^) — prev == "(", next is ")" or "^"/"*"
        //   static_cast<Foo^>  — prev == "<" (inside cast)
        if (prev is { Text: "new" }) return true;
        if (next is { Kind: CppTokenKind.Punctuation } n1
            && (n1.Text == "^" || n1.Text == "*" || n1.Text == "&" || n1.Text == "::"))
        {
            return true;
        }
        if (prev is { Text: ":" or "public" or "private" or "protected" }) return true;
        if (prev is { Text: "<" }) return true;
        if (prev is { Text: "(" } && next is { Text: ")" or "^" or "*" or "&" }) return true;
        // Foo x  or  Foo x =...  — an identifier immediately followed by another identifier
        if (next is { Kind: CppTokenKind.Identifier }) return true;
        return false;
    }

    private static bool LooksLikeCallSite(IReadOnlyList<CppToken> tokens, int idx)
    {
        var next = NextMeaningful(tokens, idx);
        return next is { Kind: CppTokenKind.Punctuation, Text: "(" };
    }

    private static bool LooksLikeMemberAccess(IReadOnlyList<CppToken> tokens, int idx)
    {
        var prev = PrevMeaningful(tokens, idx);
        // Field / property access: receiver->Name, receiver.Name, or bare Name (implicit-this).
        // Reject bare Name whose prev is a type-like keyword (declaration).
        if (prev is { Kind: CppTokenKind.Punctuation } p
            && (p.Text == "." || p.Text == "->"))
        {
            return true;
        }
        // Bare name (implicit-this) — accept when prev is a statement boundary or expression op.
        if (prev is null) return true;
        if (prev is { Text: ";" or "{" or "}" or "(" or "," or "=" or "+" or "-" or "*" or "/" or "%" or "!" or "?" or ":" or "return" or "if" or "while" or "for" or "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=" })
        {
            var next = NextMeaningful(tokens, idx);
            // Must NOT be followed by `(` (that would be a call) or another identifier (declaration).
            if (next is { Kind: CppTokenKind.Punctuation, Text: "(" }) return false;
            if (next is { Kind: CppTokenKind.Identifier }) return false;
            return true;
        }
        return false;
    }

    private static CppToken? PrevMeaningful(IReadOnlyList<CppToken> tokens, int idx)
    {
        for (int i = idx - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == CppTokenKind.EndOfFile) continue;
            return tokens[i];
        }
        return null;
    }

    private static CppToken? NextMeaningful(IReadOnlyList<CppToken> tokens, int idx)
    {
        for (int i = idx + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == CppTokenKind.EndOfFile) continue;
            return tokens[i];
        }
        return null;
    }

    private static CppReference BuildReference(CppSyntaxTree tree, CppToken token, CppReferenceKind kind)
    {
        var (line, column, snippet) = LocateInSource(tree.SourceText, token.Position, token.Length);
        return new CppReference(
            FilePath: tree.FilePath,
            Line: line,
            Column: column,
            SpanStart: token.Position,
            SpanLength: token.Length,
            LineSnippet: snippet,
            Kind: kind);
    }

    private static (int Line, int Column, string Snippet) LocateInSource(string source, int position, int length)
    {
        int lineStart = 0;
        int line = 1;
        for (int i = 0; i < position && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        int lineEnd = source.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = source.Length;
        // Strip a trailing '\r' if the file is CRLF.
        int snippetEnd = lineEnd > lineStart && source[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
        var snippet = source.Substring(lineStart, snippetEnd - lineStart).Trim();
        int column = position - lineStart + 1;
        return (line, column, snippet);
    }
}

public sealed record CppReference(
    string FilePath,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength,
    string LineSnippet,
    CppReferenceKind Kind);

public enum CppReferenceKind
{
    Declaration,
    TypeUse,
    MethodCall,
    MemberAccess,
}
