using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ReplaceConstructorWithFactoryIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public string FactoryName { get; init; } = "Create";
    public bool MakeConstructorPrivate { get; init; } = true;
}
