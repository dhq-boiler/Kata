using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Introduce Null Object": replace repeated null checks against a
// reference type with a NullObject subclass that answers all messages with
// safe defaults, so callers can invoke methods without a null check.
//
// Kata's scaffold: create `Null{SourceType}.cs` that extends SourceType and
// overrides every virtual/abstract method with a default (throw
// NotImplementedException for abstract-return, empty body for void, default
// for value returns). Users then swap `null` for `NullType.Instance` at
// creation sites and drop the null checks manually.
public sealed record IntroduceNullObjectIntent : RefactoringIntent
{
    public required TypeRef SourceType { get; init; }
    public string? NullClassName { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
