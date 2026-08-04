namespace Kata.Core.Model;

public sealed record MemberModel(
    MemberRef Ref,
    string Name,
    MemberKind Kind,
    MemberAccessibility Accessibility,
    string ReturnTypeDisplay,
    bool IsStatic,
    IReadOnlyList<ParameterModel> Parameters,
    bool IsReadOnly = false,
    bool IsGhost = false,
    bool IsMacro = false);
