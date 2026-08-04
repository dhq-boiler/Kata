using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record PullUpConstructorBodyIntent : RefactoringIntent
{
    public required TypeRef Subclass { get; init; }
    public required TypeRef Parent { get; init; }
}
