using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kata.App.Localization;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Microsoft.Win32;

namespace Kata.App.Dialogs;

public partial class DiffPreviewDialog : Window
{
    private readonly ChangeSet _changeSet;
    private readonly RefactoringIntent? _intent;

    private static readonly Brush AddedBg = new SolidColorBrush(Color.FromRgb(0x1e, 0x2a, 0x1e));
    private static readonly Brush RemovedBg = new SolidColorBrush(Color.FromRgb(0x2a, 0x1e, 0x1e));
    private static readonly Brush HunkBg = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30));
    private static readonly Brush UnchangedBg = Brushes.Transparent;

    private static readonly Brush AddedFg = new SolidColorBrush(Color.FromRgb(0xa5, 0xe2, 0xa5));
    private static readonly Brush RemovedFg = new SolidColorBrush(Color.FromRgb(0xff, 0xa0, 0xa0));
    private static readonly Brush UnchangedFg = new SolidColorBrush(Color.FromRgb(0xc0, 0xc0, 0xc0));
    private static readonly Brush HunkFg = new SolidColorBrush(Color.FromRgb(0x8e, 0xc7, 0xff));

    public DiffPreviewDialog(ChangeSet changeSet, string? rationale, string? solutionRoot, RefactoringIntent? intent = null)
    {
        InitializeComponent();

        _changeSet = changeSet;
        _intent = intent;

        SummaryText.Text = changeSet.Summary ?? $"{changeSet.Changes.Count} file change(s)";
        RationaleText.Text = string.IsNullOrEmpty(rationale) ? "(no rationale)" : $"Rationale: {rationale}";

        var entries = changeSet.Changes.Select(c => new FileEntry(c, solutionRoot)).ToArray();
        FileList.ItemsSource = entries;
        if (entries.Length > 0)
        {
            FileList.SelectedIndex = 0;
        }
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry entry)
        {
            var diff = UnifiedDiffBuilder.Build(entry.Change.OldText, entry.Change.NewText);
            DiffList.ItemsSource = diff.Select(BuildRow).ToArray();
        }
        else
        {
            DiffList.ItemsSource = null;
        }
    }

    private static DiffRowViewModel BuildRow(DiffLine line) => line.Kind switch
    {
        DiffLineKind.HunkHeader => new DiffRowViewModel(
            OldLine: string.Empty,
            NewLine: string.Empty,
            Marker: "@",
            Text: line.Text,
            RowBackground: HunkBg,
            ForegroundBrush: HunkFg,
            MarkerBrush: HunkFg),
        DiffLineKind.Added => new DiffRowViewModel(
            OldLine: string.Empty,
            NewLine: line.NewLineNumber?.ToString() ?? string.Empty,
            Marker: "+",
            Text: line.Text,
            RowBackground: AddedBg,
            ForegroundBrush: AddedFg,
            MarkerBrush: AddedFg),
        DiffLineKind.Removed => new DiffRowViewModel(
            OldLine: line.OldLineNumber?.ToString() ?? string.Empty,
            NewLine: string.Empty,
            Marker: "-",
            Text: line.Text,
            RowBackground: RemovedBg,
            ForegroundBrush: RemovedFg,
            MarkerBrush: RemovedFg),
        _ => new DiffRowViewModel(
            OldLine: line.OldLineNumber?.ToString() ?? string.Empty,
            NewLine: line.NewLineNumber?.ToString() ?? string.Empty,
            Marker: " ",
            Text: line.Text,
            RowBackground: UnchangedBg,
            ForegroundBrush: UnchangedFg,
            MarkerBrush: UnchangedFg),
    };

    private void OnApplyClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnRejectClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnExportInstructionClick(object sender, RoutedEventArgs e)
    {
        var defaultName = SanitizeFileName(_intent?.GetType().Name ?? "refactor-instruction") + ".md";
        var dialog = new SaveFileDialog
        {
            Title = "Export refactor instruction",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
            FileName = defaultName,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var markdown = InstructionExporter.ExportMarkdown(
                _intent,
                _changeSet,
                title: _intent is null ? "Refactor instruction" : $"Refactor instruction: {_intent.GetType().Name}",
                generatedAt: DateTimeOffset.Now.ToString("o"));
            File.WriteAllText(dialog.FileName, markdown);
            MessageBox.Show(this, string.Format(Strings.DiffPreview_ExportSuccess_Body, dialog.FileName), Strings.DiffPreview_ExportSuccess_Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(Strings.DiffPreview_ExportFailure_Body, ex.Message), Strings.DiffPreview_ExportFailure_Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s;
    }

    private sealed record DiffRowViewModel(
        string OldLine,
        string NewLine,
        string Marker,
        string Text,
        Brush RowBackground,
        Brush ForegroundBrush,
        Brush MarkerBrush);

    private sealed class FileEntry
    {
        public FileEntry(DocumentChange change, string? solutionRoot)
        {
            Change = change;
            DisplayName = Path.GetFileName(change.FilePath);
            SubPath = solutionRoot is not null && change.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase)
                ? change.FilePath.Substring(solutionRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : change.FilePath;
        }

        public DocumentChange Change { get; }
        public string DisplayName { get; }
        public string SubPath { get; }
    }
}
