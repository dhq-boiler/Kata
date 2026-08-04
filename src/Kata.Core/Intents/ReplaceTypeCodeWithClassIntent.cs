using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record TypeCodeEntry(string Name, string Value);

public sealed record ReplaceTypeCodeWithClassIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Field { get; init; }
    public required string NewClassName { get; init; }
    public required IReadOnlyList<TypeCodeEntry> Codes { get; init; }
    public string InnerCodeType { get; init; } = "int";
    public NamespaceRef? TargetNamespace { get; init; }
}
