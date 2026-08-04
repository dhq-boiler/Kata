using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record PushDownMethodIntent : RefactoringIntent
{
    public required TypeRef Parent { get; init; }
    public required TypeRef Subclass { get; init; }
    public required IReadOnlyList<MemberRef> Members { get; init; }
}
