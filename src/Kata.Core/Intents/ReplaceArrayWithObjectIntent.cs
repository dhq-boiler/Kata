using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ArrayFieldMapping(int Index, string FieldName, string FieldType);

public sealed record ReplaceArrayWithObjectIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ArrayField { get; init; }
    public required string NewClassName { get; init; }
    public required IReadOnlyList<ArrayFieldMapping> FieldMappings { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
