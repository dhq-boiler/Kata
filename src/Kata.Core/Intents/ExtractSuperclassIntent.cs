using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ExtractSuperclassIntent : RefactoringIntent
{
    public required TypeRef SourceType { get; init; }
    public required IReadOnlyList<MemberRef> Members { get; init; }
    public required string ProposedSuperclassName { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
