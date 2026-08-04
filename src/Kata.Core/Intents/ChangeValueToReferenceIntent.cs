using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ChangeValueToReferenceIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public string KeyType { get; init; } = "string";
    public string FactoryName { get; init; } = "GetOrCreate";
    public string RegistryFieldName { get; init; } = "_instances";
}
