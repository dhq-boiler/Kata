using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ChangeReferenceToValueIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
}
