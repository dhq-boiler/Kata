namespace Kata.Cpp.Semantics;

/// <summary>
/// Scans a C++/CLI source file for out-of-class member implementations of the
/// form <c>&lt;ret&gt; Type::Method(&lt;params&gt;) [qualifiers] { ... }</c> and reports
/// the position of the method-name identifier.
/// Best-effort — designed to pair with the header-side <see cref="CppCompilation"/>
/// so navigation can prefer the .cpp definition over the .h declaration.
/// </summary>
public static class CppImplementationLocator
{
    public readonly record struct Implementation(
        string? TypeName,          // null = file-level free function
        string MethodName,
        int ArgumentCount,
        string FilePath,
        CppSpan MethodNameSpan,
        string ParameterListText,  // raw text between ( and ) — "int a, AudioBuffer^ b"
        string ReturnTypeText);    // tokens before method name — "static bool" (best-effort, may be empty)

    public static IReadOnlyList<Implementation> Locate(string filePath, string source)
    {
        var tokens = CppCliLexer.Tokenize(source);
        var results = new List<Implementation>();

        // Track class/struct body depth so a Pattern B match nested inside an
        // inline-defined member (`class Foo { void Bar() { ... } }`) is not
        // reported as a namespace-level free function. Headers pass through the
        // same code path — without this filter, every inline member of every
        // class in the header would become a phantom "file function" and land
        // in a file-scope pseudo TypeModel.
        var classBodyDepth = ComputeClassBodyOpenBraceIndices(tokens);
        int nextClassBraceIdx = 0;
        int classDepth = 0;
        var classCloseStack = new Stack<int>();

        for (var i = 0; i < tokens.Count; i++)
        {
            // Enter class body when we hit a '{' whose position is a known class open-brace.
            if (nextClassBraceIdx < classBodyDepth.Count && classBodyDepth[nextClassBraceIdx].OpenIdx == i)
            {
                classDepth++;
                classCloseStack.Push(classBodyDepth[nextClassBraceIdx].CloseIdx);
                nextClassBraceIdx++;
            }
            // Leave when we cross the matching '}'.
            if (classCloseStack.Count > 0 && i == classCloseStack.Peek())
            {
                classCloseStack.Pop();
                classDepth--;
            }

            // Pattern A: Type::Method(...) { body } — member implementation
            if (i + 3 < tokens.Count
                && tokens[i].Kind == CppTokenKind.Identifier
                && tokens[i + 1].Kind == CppTokenKind.Punctuation && tokens[i + 1].Text == "::"
                && tokens[i + 2].Kind == CppTokenKind.Identifier
                && tokens[i + 3].Kind == CppTokenKind.Punctuation && tokens[i + 3].Text == "(")
            {
                var closeParen = SkipBalanced(tokens, i + 3, "(", ")");
                if (closeParen >= tokens.Count) continue;
                var j = SkipTrailingQualifiers(tokens, closeParen);
                j = SkipInitializerList(tokens, j);
                if (j >= tokens.Count) continue;
                if (tokens[j].Kind != CppTokenKind.Punctuation || tokens[j].Text != "{") continue;

                var argCount = CountTopLevelArguments(tokens, i + 3, closeParen);
                var methodNameToken = tokens[i + 2];
                results.Add(new Implementation(
                    TypeName: tokens[i].Text,
                    MethodName: methodNameToken.Text,
                    ArgumentCount: argCount,
                    FilePath: filePath,
                    MethodNameSpan: new CppSpan(methodNameToken.Position, methodNameToken.Length, methodNameToken.Line),
                    ParameterListText: ExtractParenContent(source, tokens, i + 3, closeParen),
                    ReturnTypeText: string.Empty));

                var bodyEnd = SkipBalanced(tokens, j, "{", "}");
                if (bodyEnd > j) i = bodyEnd - 1;
                continue;
            }

            // Pattern B: <return-type-tokens> Name(<params>) { body } — file-level function
            if (classDepth == 0
                && i + 1 < tokens.Count
                && tokens[i].Kind == CppTokenKind.Identifier
                && tokens[i + 1].Kind == CppTokenKind.Punctuation && tokens[i + 1].Text == "(")
            {
                // Must not be part of `Type::Name(...)` (already handled by Pattern A).
                if (i >= 1
                    && tokens[i - 1].Kind == CppTokenKind.Punctuation
                    && tokens[i - 1].Text == "::")
                {
                    continue;
                }
                // Previous token should look like a return-type element.
                if (i == 0) continue;
                var prev = tokens[i - 1];
                var prevLooksLikeType = prev.Kind == CppTokenKind.Identifier
                    || (prev.Kind == CppTokenKind.Punctuation && prev.Text is "^" or "*" or "&" or ">");
                if (!prevLooksLikeType) continue;

                var closeParen = SkipBalanced(tokens, i + 1, "(", ")");
                if (closeParen >= tokens.Count) continue;
                var j = SkipTrailingQualifiers(tokens, closeParen);
                if (j >= tokens.Count) continue;
                if (tokens[j].Kind != CppTokenKind.Punctuation || tokens[j].Text != "{") continue;

                var argCount = CountTopLevelArguments(tokens, i + 1, closeParen);
                var methodNameToken = tokens[i];
                results.Add(new Implementation(
                    TypeName: null,
                    MethodName: methodNameToken.Text,
                    ArgumentCount: argCount,
                    FilePath: filePath,
                    MethodNameSpan: new CppSpan(methodNameToken.Position, methodNameToken.Length, methodNameToken.Line),
                    ParameterListText: ExtractParenContent(source, tokens, i + 1, closeParen),
                    ReturnTypeText: ExtractReturnType(source, tokens, i)));

                var bodyEnd = SkipBalanced(tokens, j, "{", "}");
                if (bodyEnd > j) i = bodyEnd - 1;
            }
        }

        return results;
    }

    // Scan for `class|struct <Name> [: bases] {` openings and pair each with its matching '}'.
    // Returns (open-brace-token-index, close-brace-token-index) pairs in source order.
    // Best-effort: skips forward declarations (no '{') and ignores enum/union bodies (their
    // members aren't member functions of interest, and treating them as class bodies would
    // exclude legitimate free functions defined near them).
    private static IReadOnlyList<(int OpenIdx, int CloseIdx)> ComputeClassBodyOpenBraceIndices(IReadOnlyList<CppToken> tokens)
    {
        var pairs = new List<(int OpenIdx, int CloseIdx)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != CppTokenKind.Identifier) continue;
            if (tokens[i].Text is not ("class" or "struct")) continue;
            // Optional 'ref' / 'value' modifier already sits BEFORE 'class' — we're
            // matching on 'class' so 'ref class Foo' works too.
            // Walk forward looking for a '{' that opens the body. Stop at ';' (forward decl).
            var j = i + 1;
            while (j < tokens.Count)
            {
                var t = tokens[j];
                if (t.Kind == CppTokenKind.Punctuation && t.Text == ";") break; // forward decl
                if (t.Kind == CppTokenKind.Punctuation && t.Text == "{")
                {
                    var closeIdx = FindMatchingCloseBrace(tokens, j);
                    if (closeIdx > j)
                    {
                        pairs.Add((j, closeIdx));
                        i = j; // skip past the class keyword; nested classes get their own pass
                    }
                    break;
                }
                j++;
            }
        }
        // Sort by open-brace index (already in order, but be defensive against future edits).
        pairs.Sort((a, b) => a.OpenIdx.CompareTo(b.OpenIdx));
        return pairs;
    }

    private static int FindMatchingCloseBrace(IReadOnlyList<CppToken> tokens, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != CppTokenKind.Punctuation) continue;
            if (tokens[i].Text == "{") depth++;
            else if (tokens[i].Text == "}")
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // '(' と ')' の間の生テキストを返す。開き / 閉じ括弧自身は含めない。
    // openParenIdx はトークン列における '(' の位置、closeParenIdxExclusive は ')' の "次" の位置。
    private static string ExtractParenContent(string source, IReadOnlyList<CppToken> tokens, int openParenIdx, int closeParenIdxExclusive)
    {
        if (openParenIdx >= tokens.Count || closeParenIdxExclusive - 1 <= openParenIdx) return string.Empty;
        var open = tokens[openParenIdx];
        var close = tokens[closeParenIdxExclusive - 1];
        var start = open.Position + open.Length;
        var end = close.Position;
        if (end <= start || start < 0 || end > source.Length) return string.Empty;
        return source.Substring(start, end - start).Trim();
    }

    // メソッド名の直前にある「行内」のトークン列 (return type と修飾子) を復元する。
    // 行を跨ぐと noise を拾いすぎるので同一行 (Line プロパティが等しい) のみ拾う。
    // best-effort — 拾えなければ空文字。
    private static string ExtractReturnType(string source, IReadOnlyList<CppToken> tokens, int nameIdx)
    {
        if (nameIdx <= 0) return string.Empty;
        var nameLine = tokens[nameIdx].Line;
        var j = nameIdx - 1;
        var firstIdx = nameIdx;
        while (j >= 0 && tokens[j].Line == nameLine)
        {
            var t = tokens[j];
            // Stop at statement-terminating punctuation
            if (t.Kind == CppTokenKind.Punctuation && t.Text is ";" or "}" or "{") break;
            firstIdx = j;
            j--;
        }
        if (firstIdx == nameIdx) return string.Empty;
        var first = tokens[firstIdx];
        var last = tokens[nameIdx - 1];
        var start = first.Position;
        var end = last.Position + last.Length;
        if (start < 0 || end > source.Length || end <= start) return string.Empty;
        return source.Substring(start, end - start).Trim();
    }

    private static int SkipTrailingQualifiers(IReadOnlyList<CppToken> tokens, int start)
    {
        // "const", "override", "sealed", "abstract", "noexcept", ...
        var j = start;
        while (j < tokens.Count && tokens[j].Kind == CppTokenKind.Identifier)
        {
            j++;
        }
        return j;
    }

    private static int SkipInitializerList(IReadOnlyList<CppToken> tokens, int start)
    {
        if (start >= tokens.Count) return start;
        if (tokens[start].Kind != CppTokenKind.Punctuation || tokens[start].Text != ":")
        {
            return start;
        }
        var j = start + 1;
        while (j < tokens.Count)
        {
            var t = tokens[j];
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{")
            {
                return j;
            }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "(")
            {
                j = SkipBalanced(tokens, j, "(", ")");
                continue;
            }
            if (t.Kind == CppTokenKind.Punctuation && t.Text == "{")
            {
                return j;
            }
            j++;
        }
        return j;
    }

    private static int SkipBalanced(IReadOnlyList<CppToken> tokens, int start, string open, string close)
    {
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != CppTokenKind.Punctuation) continue;
            if (tokens[i].Text == open) depth++;
            else if (tokens[i].Text == close)
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return tokens.Count;
    }

    private static int CountTopLevelArguments(
        IReadOnlyList<CppToken> tokens,
        int openParenIdx,
        int closeParenIdxExclusive)
    {
        var depth = 0;
        var commas = 0;
        var hasContent = false;
        for (var i = openParenIdx + 1; i < closeParenIdxExclusive - 1; i++)
        {
            var t = tokens[i];
            if (t.Kind == CppTokenKind.Punctuation)
            {
                if (t.Text is "(" or "<") depth++;
                else if (t.Text is ")" or ">") depth--;
                else if (t.Text == "," && depth == 0)
                {
                    commas++;
                    continue;
                }
            }
            hasContent = true;
        }
        return hasContent ? commas + 1 : 0;
    }
}
