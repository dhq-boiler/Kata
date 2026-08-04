using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record PullUpFieldIntent : RefactoringIntent
{
    public required TypeRef Subclass { get; init; }
    public required TypeRef Parent { get; init; }
    public required IReadOnlyList<MemberRef> Members { get; init; }
}
