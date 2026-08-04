using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record CollapseHierarchyIntent : RefactoringIntent
{
    public required TypeRef Subclass { get; init; }
    public required TypeRef Parent { get; init; }
}
