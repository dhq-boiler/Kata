using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record RenameIntent : RefactoringIntent
{
    public required TypeRef TargetType { get; init; }
    public MemberRef? TargetMember { get; init; }
    public required string NewName { get; init; }
}
