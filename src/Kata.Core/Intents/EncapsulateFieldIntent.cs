using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record EncapsulateFieldIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Field { get; init; }
}
