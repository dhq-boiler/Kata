namespace Kata.Core.Diff;

public enum DocumentChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

public sealed record DocumentChange(
    string FilePath,
    DocumentChangeKind Kind,
    string? OldText,
    string? NewText,
    string? OldFilePath = null);
