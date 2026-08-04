using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record RemoveSubclassIntent : RefactoringIntent
{
    public required TypeRef Subclass { get; init; }
    public required TypeRef ReplacementBase { get; init; }
}
