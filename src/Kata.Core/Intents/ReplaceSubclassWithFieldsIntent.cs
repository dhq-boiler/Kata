using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ReplaceSubclassWithFieldsIntent : RefactoringIntent
{
    public required TypeRef ParentType { get; init; }
    public required IReadOnlyList<TypeRef> SubclassesToRemove { get; init; }
}
