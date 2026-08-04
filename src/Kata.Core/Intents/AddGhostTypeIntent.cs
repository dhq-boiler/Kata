using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record AddGhostTypeIntent : RefactoringIntent
{
    public required string ProposedName { get; init; }
    public required NamespaceRef Namespace { get; init; }
    public required TypeKind Kind { get; init; }
}
