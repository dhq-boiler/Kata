namespace Kata.Core.Diff;

// Apply hunks to a source string. Deliberately forgiving — LLMs get whitespace and line
// numbers wrong routinely, so:
//   1. Try strict line-by-line match near the hunk header's OldStart.
//   2. Fall back to full-file strict scan.
//   3. Fall back to whitespace-normalized full scan.
// If none match, throw with the hunk detail so the caller can surface the failure.
public static class UnifiedDiffPatcher
{
    public static string Apply(string original, ParsedFileDiff fileDiff)
    {
        var newline = DetectNewline(original);
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

        // Cumulative shift keeps hunk hints usable after earlier hunks change the file's shape.
        var offset = 0;

        foreach (var hunk in fileDiff.Hunks)
        {
            // LLM は「// (既存の include はそのまま)」のような自然言語 placeholder を context として
            // 挿入することがあり (実ファイルに存在しない → match 失敗の主要原因)、
            // これを context 行から除去。Removed/Added に紛れていたら意味を持ちうるので触らない。
            var trimmedLines = FilterPlaceholderContext(hunk.Lines);

            // 端の "空 context 行" を除去して needle を組む。LLM が見た目のために足す
            // 余分な空 context (端 1 行のみで hunk 全体不一致になる主要原因) を潰す。
            // Placeholder 除去後にも走らせて、placeholder が消えた結果露出した空 context も拾う。
            trimmedLines = TrimEdgeEmptyContext(trimmedLines);

            var expected = new List<string>();
            var replacement = new List<string>();
            foreach (var l in trimmedLines)
            {
                if (l.Kind is HunkLineKind.Removed or HunkLineKind.Context) expected.Add(l.Text);
                if (l.Kind is HunkLineKind.Added or HunkLineKind.Context) replacement.Add(l.Text);
            }
            if (expected.Count == 0)
            {
                // Pure insertion. Anchor by NewStart.
                var insertAt = Math.Clamp(hunk.NewStart - 1 + offset, 0, lines.Count);
                lines.InsertRange(insertAt, replacement);
                offset += replacement.Count;
                continue;
            }

            var match = FindSequence(lines, expected, hunk.OldStart - 1 + offset);
            if (match is null)
            {
                // どこまで部分一致したか (fuzzy) を返せば、ユーザーは LLM diff の
                // どの行が原ソースと食い違ってるか目視で判断できる。
                var (best, matched) = FindBestPartialFuzzy(lines, expected);
                var expectedPreview = expected.Count > 0
                    ? Truncate(expected[0], 100)
                    : "(空)";
                var breakoffPreview = matched < expected.Count
                    ? Truncate(expected[matched], 100)
                    : "(needle 全一致だが挿入位置決定できず)";
                var haystackAtBreak = matched < expected.Count && best + matched < lines.Count
                    ? Truncate(lines[best + matched], 100)
                    : "(EOF)";

                throw new InvalidOperationException(
                    $"Hunk not applicable (no match): @@ -{hunk.OldStart},{hunk.OldLen} +{hunk.NewStart},{hunk.NewLen} @@\n" +
                    $"  needle {expected.Count} 行、先頭: {expectedPreview}\n" +
                    $"  best partial: 原ソース {best + 1} 行目付近から {matched}/{expected.Count} 行一致で失敗\n" +
                    $"  needle[{matched}] 期待: {breakoffPreview}\n" +
                    $"  ファイル側の該当行     : {haystackAtBreak}");
            }

            lines.RemoveRange(match.Value.Start, match.Value.Count);
            lines.InsertRange(match.Value.Start, replacement);
            offset += replacement.Count - match.Value.Count;
        }

        return string.Join(newline, lines);
    }

    // Match は (start line, count of file lines to remove)。ほとんどの tier では count == needle.Count
    // だが、改行位置がズレて 1:1 マッチしない場合に走る tier 4 (whitespace-obliterated) では
    // count が needle.Count と異なりうる (needle 25 行が原ソースの 20 行を覆う、等)。
    private static (int Start, int Count)? FindSequence(List<string> haystack, List<string> needle, int hint)
    {
        // 1. Strict match near hint.
        var nearby = TryStrictSearch(haystack, needle, Math.Max(0, hint - 5), Math.Min(haystack.Count, hint + 20));
        if (nearby >= 0) return (nearby, needle.Count);
        // 2. Strict match full scan.
        var full = TryStrictSearch(haystack, needle, 0, haystack.Count);
        if (full >= 0) return (full, needle.Count);
        // 3. Whitespace-normalized line-by-line fuzzy scan.
        var fuzzy = TryFuzzySearch(haystack, needle);
        if (fuzzy >= 0) return (fuzzy, needle.Count);
        // 4. Whitespace-obliterated substring — 改行位置が LLM diff と原ソースで違う場合に効く。
        //    "int Foo(a,\n  b)" と "int Foo(a, b)" のような差を吸収する。
        var flat = TryFlatWhitespaceSearch(haystack, needle);
        return flat;
    }

    private static int TryStrictSearch(List<string> haystack, List<string> needle, int from, int to)
    {
        for (var i = from; i + needle.Count <= to; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int TryFuzzySearch(List<string> haystack, List<string> needle)
    {
        var normNeedle = needle.Select(NormalizeForFuzzy).ToArray();
        for (var i = 0; i + normNeedle.Length <= haystack.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < normNeedle.Length; j++)
            {
                if (!string.Equals(NormalizeForFuzzy(haystack[i + j]), normNeedle[j], StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return i;
        }
        return -1;
    }

    // 4th tier: 全空白 (space / tab / newline) を除去して substring 検索。
    // LLM が原ソースと違う位置で改行を入れた・引数を折り返した・詰めた等の
    // 「行数がズレる」パターンでマッチさせる。ヒットした char range を
    // haystack のどの line span がカバーするかを cumulative char count で逆引きし、
    // (fileStart, fileCount) を返す。
    private static (int Start, int Count)? TryFlatWhitespaceSearch(List<string> haystack, List<string> needle)
    {
        var needleFlat = new System.Text.StringBuilder();
        foreach (var n in needle) AppendNonWhitespace(needleFlat, n);
        if (needleFlat.Length == 0) return null;

        var haystackFlat = new System.Text.StringBuilder();
        // 各 haystack 行の flat 文字列の開始位置を覚えておく (line 数 + 1 要素)。
        var lineStart = new int[haystack.Count + 1];
        for (int i = 0; i < haystack.Count; i++)
        {
            lineStart[i] = haystackFlat.Length;
            AppendNonWhitespace(haystackFlat, haystack[i]);
        }
        lineStart[haystack.Count] = haystackFlat.Length;

        var needleStr = needleFlat.ToString();
        var haystackStr = haystackFlat.ToString();
        int idx = haystackStr.IndexOf(needleStr, StringComparison.Ordinal);
        if (idx < 0) return null;
        int endCharExclusive = idx + needleStr.Length;

        // idx が入る line を線形探索 (二分探索で速くできるが haystack は数千行までの想定)
        int startLine = 0;
        for (int i = 0; i < haystack.Count; i++)
        {
            if (lineStart[i] <= idx && idx < lineStart[i + 1]) { startLine = i; break; }
            // 空行 (lineStart[i] == lineStart[i+1]) の場合、次の非空行を採用
            if (lineStart[i] == idx && lineStart[i] == lineStart[i + 1]) { startLine = i; break; }
        }
        int endLineExclusive = startLine + 1;
        for (int i = startLine; i < haystack.Count; i++)
        {
            if (endCharExclusive <= lineStart[i + 1]) { endLineExclusive = i + 1; break; }
        }
        return (startLine, endLineExclusive - startLine);
    }

    private static void AppendNonWhitespace(System.Text.StringBuilder sb, string line)
    {
        foreach (var c in line)
        {
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        }
    }

    private static string NormalizeForFuzzy(string line)
    {
        // Collapse runs of whitespace + trim ends. Keeps the identity of the code while
        // ignoring indentation drift the LLM often inflicts on hunk bodies.
        var trimmed = line.Trim();
        var sb = new System.Text.StringBuilder(trimmed.Length);
        var prevSpace = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    private static string DetectNewline(string text)
    {
        if (text.Contains("\r\n", StringComparison.Ordinal)) return "\r\n";
        return "\n";
    }

    // Fuzzy 最良一致位置と一致数を返す。失敗時の診断メッセージ用。
    // 全 haystack を走査 (O(n*m)) するが patch 失敗は稀 & 診断時のみ通るので許容。
    private static (int BestStart, int MatchedCount) FindBestPartialFuzzy(List<string> haystack, List<string> needle)
    {
        var normNeedle = needle.Select(NormalizeForFuzzy).ToArray();
        var bestStart = 0;
        var bestMatched = 0;
        for (var i = 0; i + normNeedle.Length <= haystack.Count; i++)
        {
            var matched = 0;
            for (var j = 0; j < normNeedle.Length; j++)
            {
                if (!string.Equals(NormalizeForFuzzy(haystack[i + j]), normNeedle[j], StringComparison.Ordinal)) break;
                matched++;
            }
            if (matched > bestMatched)
            {
                bestMatched = matched;
                bestStart = i;
                if (bestMatched == normNeedle.Length) break;
            }
        }
        return (bestStart, bestMatched);
    }

    private static string Truncate(string s, int max)
    {
        var single = s.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= max ? single : single[..max] + "…";
    }

    // LLM が context として挿入した自然言語 placeholder コメントを丸ごと除去する。
    // 該当パターン: 「// (...)」「// ... 省略 ...」「/* ... */」の "..." で始まる、
    // 「(既存の...)」「(rest stays...)」など。実ファイルには存在しない指示的注釈で、
    // これが needle に混ざると必ず match 失敗する (実際 log #9 の主要原因)。
    // Context 行のみを対象にする — Removed / Added に紛れていたら本物のコード変更と
    // 見分けがつかないので触らない。
    private static IReadOnlyList<ParsedHunkLine> FilterPlaceholderContext(IReadOnlyList<ParsedHunkLine> lines)
    {
        List<ParsedHunkLine>? filtered = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Kind == HunkLineKind.Context && LooksLikePlaceholderComment(l.Text))
            {
                if (filtered is null)
                {
                    filtered = new List<ParsedHunkLine>(lines.Count - 1);
                    for (int j = 0; j < i; j++) filtered.Add(lines[j]);
                }
                continue;
            }
            filtered?.Add(l);
        }
        return filtered ?? lines;
    }

    // 「// (...)」「// ...省略...」「// existing」「// rest unchanged」等、
    // LLM が慣用的に diff 中に挿入する placeholder パターンかどうか。false positive を
    // 避けるため、実際のコメント風でも中身が「素直な英文/和文の要約」に見えるものだけ拾う。
    private static bool LooksLikePlaceholderComment(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith("//", StringComparison.Ordinal)) return false;
        var body = t.Substring(2).Trim();
        if (body.Length == 0) return false;
        // (…) で始まって ) で終わる — LLM 常套パターン (log #9)
        if (body.StartsWith("(", StringComparison.Ordinal) && body.EndsWith(")", StringComparison.Ordinal))
            return true;
        // 省略 / 略 / 以下同 / 以下略 / 中略 / 前略 / 後略 を含む
        if (body.Contains("省略", StringComparison.Ordinal)) return true;
        if (body.Contains("以下同", StringComparison.Ordinal)) return true;
        if (body.Contains("以下略", StringComparison.Ordinal)) return true;
        if (body.Contains("中略", StringComparison.Ordinal)) return true;
        if (body.Contains("前略", StringComparison.Ordinal)) return true;
        if (body.Contains("後略", StringComparison.Ordinal)) return true;
        // "..." で始まる (ellipsis pattern)
        if (body.StartsWith("...", StringComparison.Ordinal)) return true;
        // "…" (U+2026) を含む
        if (body.Contains('…')) return true;
        // "existing " / "unchanged " / "rest of " / "same as " / "keep " などで始まる英語
        if (body.StartsWith("existing ", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("unchanged", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("rest of", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("same as", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("keep ", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("no change", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    // hunk の先頭 / 末尾にある空白のみの Context 行を落として返す。Removed / Added 行は
    // 空白でも意味 (「空行を削除」「空行を追加」) があるので保持。中央の空 Context も
    // 位置情報として残す。
    private static IReadOnlyList<ParsedHunkLine> TrimEdgeEmptyContext(IReadOnlyList<ParsedHunkLine> lines)
    {
        int start = 0;
        while (start < lines.Count
               && lines[start].Kind == HunkLineKind.Context
               && string.IsNullOrWhiteSpace(lines[start].Text))
        {
            start++;
        }
        int endExclusive = lines.Count;
        while (endExclusive > start
               && lines[endExclusive - 1].Kind == HunkLineKind.Context
               && string.IsNullOrWhiteSpace(lines[endExclusive - 1].Text))
        {
            endExclusive--;
        }
        if (start == 0 && endExclusive == lines.Count) return lines;

        var trimmed = new List<ParsedHunkLine>(endExclusive - start);
        for (int i = start; i < endExclusive; i++) trimmed.Add(lines[i]);
        return trimmed;
    }
}
