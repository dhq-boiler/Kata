namespace Kata.Core.Model;

public readonly record struct NamespaceRef(string FullName)
{
    public static NamespaceRef Global { get; } = new(string.Empty);

    public bool IsGlobal => string.IsNullOrEmpty(FullName);

    public override string ToString() => IsGlobal ? "<global>" : FullName;
}
