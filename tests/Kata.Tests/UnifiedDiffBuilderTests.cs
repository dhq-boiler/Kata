using Kata.Core.Diff;

namespace Kata.Tests;

public sealed class UnifiedDiffBuilderTests
{
    [Fact]
    public void Empty_when_texts_are_identical()
    {
        var d = UnifiedDiffBuilder.Build("a\nb\nc\n", "a\nb\nc\n");
        Assert.Empty(d);
    }

    [Fact]
    public void Renaming_one_identifier_yields_small_hunk_with_context()
    {
        const string before = """
            line1
            line2
            void Connect(int x);
            line4
            line5
            """;
        const string after = """
            line1
            line2
            void Link(int x);
            line4
            line5
            """;
        var d = UnifiedDiffBuilder.Build(before, after);

        Assert.Contains(d, l => l.Kind == DiffLineKind.HunkHeader);
        Assert.Contains(d, l => l.Kind == DiffLineKind.Removed && l.Text.Contains("Connect"));
        Assert.Contains(d, l => l.Kind == DiffLineKind.Added && l.Text.Contains("Link"));
        // Unchanged context around the change survives.
        Assert.Contains(d, l => l.Kind == DiffLineKind.Unchanged && l.Text == "line2");
        Assert.Contains(d, l => l.Kind == DiffLineKind.Unchanged && l.Text == "line4");
    }

    [Fact]
    public void Multiple_hunks_appear_when_changes_are_far_apart()
    {
        var beforeLines = Enumerable.Range(1, 30).Select(i => i == 5 ? "OLD_A" : i == 25 ? "OLD_B" : $"line{i}");
        var afterLines = Enumerable.Range(1, 30).Select(i => i == 5 ? "NEW_A" : i == 25 ? "NEW_B" : $"line{i}");

        var d = UnifiedDiffBuilder.Build(string.Join('\n', beforeLines), string.Join('\n', afterLines));

        var hunks = d.Count(l => l.Kind == DiffLineKind.HunkHeader);
        Assert.Equal(2, hunks);
    }

    [Fact]
    public void Line_numbers_are_populated_correctly()
    {
        var d = UnifiedDiffBuilder.Build("a\nb\nc\n", "a\nX\nc\n");
        // Should include an unchanged line "a" with OldLineNumber=1, NewLineNumber=1.
        Assert.Contains(d, l => l.Kind == DiffLineKind.Unchanged && l.OldLineNumber == 1 && l.NewLineNumber == 1);
        // Removed "b" has OldLineNumber=2.
        Assert.Contains(d, l => l.Kind == DiffLineKind.Removed && l.Text == "b" && l.OldLineNumber == 2 && l.NewLineNumber is null);
        // Added "X" has NewLineNumber=2.
        Assert.Contains(d, l => l.Kind == DiffLineKind.Added && l.Text == "X" && l.NewLineNumber == 2 && l.OldLineNumber is null);
    }
}
