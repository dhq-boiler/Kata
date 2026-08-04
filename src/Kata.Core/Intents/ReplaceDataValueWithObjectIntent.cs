using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ReplaceDataValueWithObjectIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Field { get; init; }
    public required string WrapperClassName { get; init; }
    public string InnerFieldName { get; init; } = "Value";
    public NamespaceRef? TargetNamespace { get; init; }
}
