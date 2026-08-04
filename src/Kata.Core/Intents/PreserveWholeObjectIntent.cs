using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record PreserveWholeObjectIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Method { get; init; }
    public required TypeRef ObjectType { get; init; }
    public required string ParameterName { get; init; }
    public required IReadOnlyList<string> ReplacedParameterNames { get; init; }
}
