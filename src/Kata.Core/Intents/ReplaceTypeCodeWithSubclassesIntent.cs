using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ReplaceTypeCodeWithSubclassesIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required IReadOnlyList<string> SubclassNames { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
