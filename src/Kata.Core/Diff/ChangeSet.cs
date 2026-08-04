using Kata.Core.Intents;

namespace Kata.Core.Diff;

public sealed record ChangeSet(
    IReadOnlyList<Guid> AppliedIntentIds,
    IReadOnlyList<DocumentChange> Changes,
    string? Summary = null)
{
    public static ChangeSet Empty { get; } = new(
        Array.Empty<Guid>(),
        Array.Empty<DocumentChange>());
}
