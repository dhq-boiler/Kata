using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ExtractInterfaceIntent : RefactoringIntent
{
    public required TypeRef SourceType { get; init; }
    public required IReadOnlyList<MemberRef> Members { get; init; }
    public required string ProposedInterfaceName { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
