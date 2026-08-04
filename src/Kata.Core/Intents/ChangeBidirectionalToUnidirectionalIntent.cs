using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ChangeBidirectionalToUnidirectionalIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Field { get; init; }
}
