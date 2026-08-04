namespace Kata.Core.Model;

public sealed record MemberSource(
    TypeRef OwnerType,
    MemberRef Member,
    string FilePath,
    string SourceText,
    int MemberSpanStart,
    int MemberSpanLength,
    int BodySpanStart,
    int BodySpanLength);
