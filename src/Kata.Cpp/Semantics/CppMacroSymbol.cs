namespace Kata.Cpp.Semantics;

/// <summary>
/// A C/C++ preprocessor macro (<c>#define NAME [(params)] [replacement]</c>) captured
/// from a .h/.cpp source file. Not part of the C++ type system, but surfaced on the
/// class diagram as a file-scope pseudo type member with the «macro» stereotype
/// so refactoring / Impact Focus can reason about macro dependencies.
/// </summary>
public sealed record CppMacroSymbol(
    string Name,
    bool IsFunctionLike,
    string ReplacementText,
    IReadOnlyList<string> Parameters,
    CppDeclarationSite Site);
