using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record MoveFieldIntent : RefactoringIntent
{
    public required TypeRef SourceType { get; init; }
    public required TypeRef TargetType { get; init; }
    public required IReadOnlyList<MemberRef> Members { get; init; }
}
