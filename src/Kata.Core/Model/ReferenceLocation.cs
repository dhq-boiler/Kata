namespace Kata.Core.Model;

/// <summary>
/// A single occurrence of a symbol in source. Used by find-references to
/// return uniform results across languages (C# via Roslyn, C++/CLI via
/// <c>Kata.Cpp.Semantics.CppReferenceFinder</c>).
/// </summary>
public sealed record ReferenceLocation(
    string FilePath,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength,
    string LineSnippet,
    ReferenceKind Kind,
    ReferenceLanguage Language);

public enum ReferenceKind
{
    Declaration,
    TypeUse,
    MethodCall,
    MemberAccess,
}

public enum ReferenceLanguage
{
    CSharp,
    CppCli,
}
