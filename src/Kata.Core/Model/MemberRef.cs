namespace Kata.Core.Model;

public readonly record struct MemberRef(TypeRef DeclaringType, string Signature)
{
    public override string ToString() => $"{DeclaringType}.{Signature}";
}
