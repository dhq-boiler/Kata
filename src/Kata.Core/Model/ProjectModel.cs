namespace Kata.Core.Model;

public sealed record ProjectModel(
    string Name,
    string FilePath,
    string LanguageId,
    IReadOnlyList<TypeModel> Types);
