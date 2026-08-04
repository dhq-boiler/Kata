using System.Xml.Linq;

namespace Kata.Cpp.Syntax;

public static class CppCliProjectLoader
{
    public static IReadOnlyList<string> EnumerateHeaders(string vcxprojPath)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(vcxprojPath);
        if (dir is null)
        {
            return results;
        }

        try
        {
            var doc = XDocument.Load(vcxprojPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            foreach (var incl in doc.Descendants(ns + "ClInclude"))
            {
                var relative = incl.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(relative))
                {
                    continue;
                }
                var full = Path.GetFullPath(Path.Combine(dir, relative.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(full) && seen.Add(full))
                {
                    results.Add(full);
                }
            }
        }
        catch
        {
            // Fall through — best-effort.
        }

        // vcxproj に載っていない .h も常にディレクトリスキャンして拾う。
        // AI diff で新規 helper header (例: SourceConnectHelper.h) が
        // vcxproj 未更新のまま追加された場合でも、次の CppCompilation 再構築で
        // ちゃんとクラス図に現れるように。以前は vcxproj エントリが 1 個でもあれば
        // fallback しない実装だった。
        foreach (var h in Directory.EnumerateFiles(dir, "*.h", SearchOption.AllDirectories))
        {
            if (h.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Contains(Path.DirectorySeparatorChar + "x64" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Contains(Path.DirectorySeparatorChar + "x86" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(h)) results.Add(h);
        }

        return results;
    }

    // Leave one CPU core for the UI thread / OS so the cursor doesn't stall
    // during large solution loads.
    private static readonly int MaxParseParallelism = Math.Max(1, Environment.ProcessorCount - 1);

    public static IReadOnlyList<CppSyntaxTree> LoadSyntaxTrees(string vcxprojPath)
    {
        // Fan-out: parse every header in parallel — each Parse call is pure,
        // no shared state, no cross-file dependency. Gather non-null results.
        return EnumerateHeaders(vcxprojPath)
            .AsParallel()
            .WithDegreeOfParallelism(MaxParseParallelism)
            .Select(TryParseFile)
            .Where(t => t is not null)
            .Cast<CppSyntaxTree>()
            .ToList();
    }

    private static CppSyntaxTree? TryParseFile(string path)
    {
        try { return CppSyntaxTree.ParseFile(path); }
        catch { return null; }
    }

    public static IReadOnlyList<string> EnumerateSourceFiles(string vcxprojPath)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(vcxprojPath);
        if (dir is null)
        {
            return results;
        }

        try
        {
            var doc = XDocument.Load(vcxprojPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            foreach (var incl in doc.Descendants(ns + "ClCompile"))
            {
                var relative = incl.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(relative))
                {
                    continue;
                }
                var full = Path.GetFullPath(Path.Combine(dir, relative.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(full) && seen.Add(full))
                {
                    results.Add(full);
                }
            }
        }
        catch
        {
            // best-effort
        }

        // vcxproj に載っていない .cpp も常にディレクトリスキャンして拾う
        // (新規 impl file を AI diff で追加された場合の救済)。
        foreach (var c in Directory.EnumerateFiles(dir, "*.cpp", SearchOption.AllDirectories))
        {
            if (c.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Contains(Path.DirectorySeparatorChar + "x64" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Contains(Path.DirectorySeparatorChar + "x86" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(c)) results.Add(c);
        }

        return results;
    }

    public static IReadOnlyList<CppSyntaxTree> LoadImplementationTrees(string vcxprojPath)
    {
        return EnumerateSourceFiles(vcxprojPath)
            .AsParallel()
            .WithDegreeOfParallelism(MaxParseParallelism)
            .Select(TryParseFile)
            .Where(t => t is not null)
            .Cast<CppSyntaxTree>()
            .ToList();
    }
}
