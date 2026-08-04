using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record SelfEncapsulateFieldIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Field { get; init; }
    public string? PropertyName { get; init; }
}
