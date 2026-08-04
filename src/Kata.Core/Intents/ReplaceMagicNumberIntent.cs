using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record ReplaceMagicNumberIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required string LiteralValue { get; init; }
    public required string ConstantName { get; init; }
    public string ConstantType { get; init; } = "int";
}
