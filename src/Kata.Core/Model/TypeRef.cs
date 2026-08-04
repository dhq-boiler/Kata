namespace Kata.Core.Model;

public readonly record struct TypeRef(string FullyQualifiedName)
{
    public override string ToString() => FullyQualifiedName;
}
