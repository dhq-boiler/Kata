using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record RenameParameterIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Method { get; init; }
    public required string OldName { get; init; }
    public required string NewName { get; init; }
}
