using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ExtractClassIntent : RefactoringIntent
{
    public required TypeRef SourceType { get; init; }
    public required IReadOnlyList<MemberRef> Members { get; init; }
    public required string ProposedClassName { get; init; }
    public required string DelegatePropertyName { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
