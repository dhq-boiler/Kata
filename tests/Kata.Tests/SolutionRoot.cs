namespace Kata.Tests;

internal static class SolutionRoot
{
    public static string GetKataSolutionPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Kata.slnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Kata.slnx not found by walking up from AppContext.BaseDirectory.");
    }
}
