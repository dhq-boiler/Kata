using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record RemoveParameterIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Method { get; init; }
    public required string ParameterName { get; init; }
}
