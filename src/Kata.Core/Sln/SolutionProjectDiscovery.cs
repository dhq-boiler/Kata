using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kata.Core.Sln;

public sealed record DiscoveredProject(string Name, string AbsolutePath, string Extension);

public static class SolutionProjectDiscovery
{
    public static IReadOnlyList<DiscoveredProject> DiscoverForeignProjects(
        string solutionPath,
        IReadOnlySet<string> foreignExtensions)
    {
        var entries = ParseSolutionEntries(solutionPath);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var result = new List<DiscoveredProject>();
        foreach (var (relPath, name) in entries)
        {
            var ext = Path.GetExtension(relPath);
            if (!foreignExtensions.Contains(ext))
            {
                continue;
            }

            var absolute = Path.GetFullPath(Path.Combine(solutionDir, relPath));
            var displayName = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(relPath) : name;
            result.Add(new DiscoveredProject(displayName, absolute, ext));
        }
        return result;
    }

    private static IReadOnlyList<(string RelPath, string Name)> ParseSolutionEntries(string solutionPath)
    {
        var ext = Path.GetExtension(solutionPath);
        return ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ParseSlnx(solutionPath)
            : ParseSln(solutionPath);
    }

    private static IReadOnlyList<(string RelPath, string Name)> ParseSlnx(string slnxPath)
    {
        var doc = XDocument.Load(slnxPath);
        var entries = new List<(string, string)>();
        foreach (var project in doc.Descendants("Project"))
        {
            var path = project.Attribute("Path")?.Value;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            var name = Path.GetFileNameWithoutExtension(path);
            entries.Add((NormalizePath(path), name));
        }
        return entries;
    }

    private static readonly Regex SlnProjectLine = new(
        "^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"([^\"]*)\"\\s*,\\s*\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IReadOnlyList<(string RelPath, string Name)> ParseSln(string slnPath)
    {
        var text = File.ReadAllText(slnPath);
        var entries = new List<(string, string)>();
        foreach (Match m in SlnProjectLine.Matches(text))
        {
            var name = m.Groups[1].Value;
            var path = m.Groups[2].Value;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            entries.Add((NormalizePath(path), name));
        }
        return entries;
    }

    private static string NormalizePath(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
