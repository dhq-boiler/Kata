using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record RenameFieldIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Field { get; init; }
    public required string NewName { get; init; }
}
