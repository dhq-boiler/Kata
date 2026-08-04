using System.Text.RegularExpressions;

namespace Kata.Core.Diff;

// Small hand-rolled unified diff parser. LLM output tends to be forgiving in shape (missing
// hunk header line counts, extra prose, mixed newlines) so this parser is deliberately
// permissive: it recovers from a broken hunk header by scanning until the next known marker.
public static class UnifiedDiffParser
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(?<oldStart>\d+)(?:,(?<oldLen>\d+))?\s+\+(?<newStart>\d+)(?:,(?<newLen>\d+))?\s+@@",
        RegexOptions.Compiled);

    public static ParsedDiff Parse(string diffText)
    {
        var files = new List<ParsedFileDiff>();
        var lines = diffText.Replace("\r\n", "\n").Split('\n');

        string? oldPath = null;
        string? newPath = null;
        List<ParsedHunk>? hunks = null;
        List<ParsedHunkLine>? currentHunkLines = null;
        int hunkOldStart = 0, hunkOldLen = 0, hunkNewStart = 0, hunkNewLen = 0;

        void CloseCurrentHunk()
        {
            if (currentHunkLines is null) return;
            hunks ??= new List<ParsedHunk>();
            hunks.Add(new ParsedHunk(hunkOldStart, hunkOldLen, hunkNewStart, hunkNewLen, currentHunkLines));
            currentHunkLines = null;
        }

        void CloseCurrentFile()
        {
            CloseCurrentHunk();
            // Accept a file entry as long as it has hunks. LLMs frequently omit the
            // `--- a/... / +++ b/...` header and jump straight to `@@` — earlier we dropped
            // those on the floor and reported "no hunks", which was misleading. When no
            // header was seen, OldPath/NewPath end up empty and the caller
            // (ResolveDiffFilePath) falls back to the smell's own source file.
            if (hunks is not null && hunks.Count > 0)
            {
                files.Add(new ParsedFileDiff(
                    OldPath: StripDiffPrefix(oldPath ?? newPath ?? string.Empty),
                    NewPath: StripDiffPrefix(newPath ?? oldPath ?? string.Empty),
                    Hunks: hunks));
            }
            hunks = null;
            oldPath = null;
            newPath = null;
        }

        foreach (var raw in lines)
        {
            if (raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                CloseCurrentFile();
                oldPath = raw[4..].Trim();
                continue;
            }
            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = raw[4..].Trim();
                continue;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                // Two shapes accepted:
                //   1. Canonical: @@ -oldStart,oldLen +newStart,newLen @@
                //   2. LLM-truncated: bare @@ (no line numbers, no closing @@).
                // Claude/Codex regularly omit the line numbers because they can't
                // reliably compute them from a snippet. Take whatever hits, default
                // the missing fields so the patcher falls back to a full-file scan.
                CloseCurrentHunk();
                var match = HunkHeaderRegex.Match(raw);
                if (match.Success)
                {
                    hunkOldStart = int.Parse(match.Groups["oldStart"].Value);
                    hunkOldLen = match.Groups["oldLen"].Success ? int.Parse(match.Groups["oldLen"].Value) : 1;
                    hunkNewStart = int.Parse(match.Groups["newStart"].Value);
                    hunkNewLen = match.Groups["newLen"].Success ? int.Parse(match.Groups["newLen"].Value) : 1;
                }
                else
                {
                    hunkOldStart = 1;
                    hunkOldLen = 0;
                    hunkNewStart = 1;
                    hunkNewLen = 0;
                }
                currentHunkLines = new List<ParsedHunkLine>();
                continue;
            }

            if (currentHunkLines is null) continue;

            if (raw.Length == 0)
            {
                // Blank line inside a hunk is a context line in canonical unified diff.
                // Some LLMs emit it without the leading space; accept either.
                currentHunkLines.Add(new ParsedHunkLine(HunkLineKind.Context, string.Empty));
                continue;
            }
            var marker = raw[0];
            var text = raw.Length > 1 ? raw[1..] : string.Empty;
            switch (marker)
            {
                case '+':
                    currentHunkLines.Add(new ParsedHunkLine(HunkLineKind.Added, text));
                    break;
                case '-':
                    currentHunkLines.Add(new ParsedHunkLine(HunkLineKind.Removed, text));
                    break;
                case ' ':
                    currentHunkLines.Add(new ParsedHunkLine(HunkLineKind.Context, text));
                    break;
                default:
                    // Non-marker line — assume end of hunk (LLM narrative resumed).
                    CloseCurrentHunk();
                    break;
            }
        }

        CloseCurrentFile();
        return new ParsedDiff(files);
    }

    // Extract diff bodies from an LLM response. Recognises ```diff ... ``` fenced code first —
    // collects ALL such blocks (LLM often splits into 3 fenced blocks: .h + .cpp #1 + .cpp #2)
    // and concatenates them with a blank line separator so the parser sees a single stream.
    // Falls back to scanning for the first `--- ` line and taking everything until the end.
    public static string? ExtractDiffBlock(string llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse)) return null;

        // 1. ALL fenced ```diff / ```patch blocks. Previously we returned only the first match,
        //    which dropped subsequent per-file diffs when the LLM split them (very common when
        //    an Extract Method touches both .h and .cpp — Claude tends to open a fresh
        //    fenced block per file).
        var fencedMatches = Regex.Matches(llmResponse,
            @"```\s*(?:diff|patch)?\s*\n(?<body>[\s\S]*?)```",
            RegexOptions.IgnoreCase);
        if (fencedMatches.Count > 0)
        {
            var bodies = new List<string>();
            foreach (Match m in fencedMatches)
            {
                var body = m.Groups["body"].Value;
                if (body.Contains("--- ", StringComparison.Ordinal)
                    || body.Contains("@@ ", StringComparison.Ordinal))
                {
                    bodies.Add(body);
                }
            }
            if (bodies.Count > 0) return string.Join("\n\n", bodies);
        }

        // 2. First occurrence of `--- ` header (no fenced blocks — whole tail is diff-ish).
        var idx = llmResponse.IndexOf("--- ", StringComparison.Ordinal);
        if (idx >= 0) return llmResponse[idx..];

        // 3. Bare @@ hunks (path-less diff).
        idx = llmResponse.IndexOf("@@ ", StringComparison.Ordinal);
        if (idx >= 0) return llmResponse[idx..];

        return null;
    }

    private static string StripDiffPrefix(string path)
    {
        if (path.StartsWith("a/", StringComparison.Ordinal) ||
            path.StartsWith("b/", StringComparison.Ordinal))
        {
            return path[2..];
        }
        return path;
    }
}

public sealed record ParsedDiff(IReadOnlyList<ParsedFileDiff> Files);

public sealed record ParsedFileDiff(
    string OldPath,
    string NewPath,
    IReadOnlyList<ParsedHunk> Hunks);

public sealed record ParsedHunk(
    int OldStart,
    int OldLen,
    int NewStart,
    int NewLen,
    IReadOnlyList<ParsedHunkLine> Lines);

public sealed record ParsedHunkLine(HunkLineKind Kind, string Text);

public enum HunkLineKind
{
    Context,
    Added,
    Removed,
}
