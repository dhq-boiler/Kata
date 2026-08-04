namespace Kata.Core.Model;

public sealed record SolutionModel(
    string FilePath,
    IReadOnlyList<ProjectModel> Projects);
