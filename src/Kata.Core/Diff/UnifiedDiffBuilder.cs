namespace Kata.Core.Diff;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
    HunkHeader,
}

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber);

/// <summary>
/// Builds a unified-diff view (with N-line context around each change) from two
/// full-file texts. LCS-based line matching; O(N*M) time and memory in line counts,
/// fine for typical source files.
/// </summary>
public static class UnifiedDiffBuilder
{
    public static IReadOnlyList<DiffLine> Build(string? oldText, string? newText, int contextLines = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var script = ComputeEditScript(oldLines, newLines);
        return AssembleHunks(script, contextLines);
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        // Preserve empty trailing line intent by using StringSplitOptions.None.
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    // Produce an ordered edit script: (Kind, OldLineNo?, NewLineNo?, Text).
    // Kind can be Unchanged/Added/Removed.
    private static List<DiffLine> ComputeEditScript(string[] a, string[] b)
    {
        // Standard LCS DP. Rows = a.Length + 1, Cols = b.Length + 1.
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int ai = 0, bi = 0;
        while (ai < n && bi < m)
        {
            if (string.Equals(a[ai], b[bi], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffLineKind.Unchanged, a[ai], ai + 1, bi + 1));
                ai++; bi++;
            }
            else if (dp[ai + 1, bi] >= dp[ai, bi + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, a[ai], ai + 1, null));
                ai++;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, b[bi], null, bi + 1));
                bi++;
            }
        }
        while (ai < n)
        {
            result.Add(new DiffLine(DiffLineKind.Removed, a[ai], ai + 1, null));
            ai++;
        }
        while (bi < m)
        {
            result.Add(new DiffLine(DiffLineKind.Added, b[bi], null, bi + 1));
            bi++;
        }
        return result;
    }

    private static IReadOnlyList<DiffLine> AssembleHunks(List<DiffLine> script, int context)
    {
        if (script.Count == 0) return Array.Empty<DiffLine>();
        // Fully equal (rename didn't actually touch this file? shouldn't happen but be safe).
        if (script.All(l => l.Kind == DiffLineKind.Unchanged))
        {
            return Array.Empty<DiffLine>();
        }

        // Which lines are "interesting" (changes) — keep them plus ±context.
        var keep = new bool[script.Count];
        for (int i = 0; i < script.Count; i++)
        {
            if (script[i].Kind == DiffLineKind.Unchanged) continue;
            int lo = Math.Max(0, i - context);
            int hi = Math.Min(script.Count - 1, i + context);
            for (int j = lo; j <= hi; j++) keep[j] = true;
        }

        var output = new List<DiffLine>();
        int idx = 0;
        while (idx < script.Count)
        {
            while (idx < script.Count && !keep[idx]) idx++;
            if (idx >= script.Count) break;

            int start = idx;
            while (idx < script.Count && keep[idx]) idx++;
            int end = idx; // exclusive

            var slice = script.GetRange(start, end - start);
            int oldStart = slice.FirstOrDefault(l => l.OldLineNumber.HasValue)?.OldLineNumber ?? (start + 1);
            int newStart = slice.FirstOrDefault(l => l.NewLineNumber.HasValue)?.NewLineNumber ?? (start + 1);
            int oldCount = slice.Count(l => l.Kind != DiffLineKind.Added);
            int newCount = slice.Count(l => l.Kind != DiffLineKind.Removed);

            output.Add(new DiffLine(
                DiffLineKind.HunkHeader,
                $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@",
                null, null));
            output.AddRange(slice);
        }
        return output;
    }
}
