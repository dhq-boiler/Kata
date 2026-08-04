using Kata.Core.Model;

namespace Kata.Core.Intents;

public sealed record RemoveSettingMethodIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef Property { get; init; }
}
