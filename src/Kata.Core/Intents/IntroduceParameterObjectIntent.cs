using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record IntroduceParameterObjectIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Method { get; init; }
    public required string ProposedObjectName { get; init; }
    public string ParameterName { get; init; } = "args";
    public NamespaceRef? TargetNamespace { get; init; }
}
