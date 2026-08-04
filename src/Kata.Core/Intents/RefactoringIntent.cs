namespace Kata.Core.Intents;

public abstract record RefactoringIntent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required IntentSource Source { get; init; }
    public string? Rationale { get; init; }
}
