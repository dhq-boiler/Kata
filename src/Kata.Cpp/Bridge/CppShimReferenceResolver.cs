using System.Xml.Linq;

namespace Kata.Cpp.Bridge;

/// <summary>
/// Locates a build-output DLL for a C++/CLI vcxproj that is fresh enough to
/// be trusted as a Roslyn MetadataReference, so the C# side no longer sees
/// error types for cross-language members.
/// </summary>
public static class CppShimReferenceResolver
{
    private static readonly string[] KnownConfigs =
    {
        "Debug",
        "Release",
        "Release(NoObfuscated)",
    };

    /// <summary>
    /// Returns the newest DLL under x64/{Config}/{ProjectName}.dll whose
    /// LastWriteTimeUtc is not older than any .h/.cpp source under the
    /// project directory. Returns null if no candidate qualifies.
    /// </summary>
    public static string? TryResolveFreshDll(string vcxprojPath)
    {
        var result = ResolveDll(vcxprojPath);
        return result.IsFresh ? result.DllPath : null;
    }

    /// <summary>
    /// Wider variant that returns any usable DLL even when it is older than
    /// the source (stale). Callers get to decide whether to inject a stale DLL
    /// (and surface a warning to the user) vs give up on Cpp injection entirely.
    /// A rename apply mutates the sources faster than the DLL, so this stale-tolerant
    /// path is important — without it every rename would self-brick the next rename
    /// until the user rebuilt the C++/CLI project.
    /// </summary>
    public static CppShimResolutionResult ResolveDll(string vcxprojPath)
    {
        if (string.IsNullOrEmpty(vcxprojPath) || !File.Exists(vcxprojPath))
        {
            return CppShimResolutionResult.None;
        }

        var projectDir = Path.GetDirectoryName(vcxprojPath);
        if (string.IsNullOrEmpty(projectDir))
        {
            return CppShimResolutionResult.None;
        }

        var projectName = ReadProjectName(vcxprojPath) ?? Path.GetFileNameWithoutExtension(vcxprojPath);
        var newestSourceUtc = NewestSourceUtc(projectDir);

        string? bestFreshDll = null;
        DateTime bestFreshUtc = DateTime.MinValue;
        string? bestAnyDll = null;
        DateTime bestAnyUtc = DateTime.MinValue;

        foreach (var cfg in KnownConfigs)
        {
            var dllPath = Path.Combine(projectDir, "x64", cfg, projectName + ".dll");
            if (!File.Exists(dllPath)) continue;

            var dllUtc = File.GetLastWriteTimeUtc(dllPath);
            if (dllUtc > bestAnyUtc)
            {
                bestAnyUtc = dllUtc;
                bestAnyDll = dllPath;
            }
            bool isFresh = !newestSourceUtc.HasValue || dllUtc >= newestSourceUtc.Value;
            if (isFresh && dllUtc > bestFreshUtc)
            {
                bestFreshUtc = dllUtc;
                bestFreshDll = dllPath;
            }
        }

        if (bestFreshDll is not null)
        {
            return new CppShimResolutionResult(bestFreshDll, IsFresh: true, DllUtc: bestFreshUtc, NewestSourceUtc: newestSourceUtc);
        }
        if (bestAnyDll is not null)
        {
            return new CppShimResolutionResult(bestAnyDll, IsFresh: false, DllUtc: bestAnyUtc, NewestSourceUtc: newestSourceUtc);
        }
        return CppShimResolutionResult.None;
    }

    private static string? ReadProjectName(string vcxprojPath)
    {
        try
        {
            var doc = XDocument.Load(vcxprojPath);
            XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
            var el = doc.Descendants(ns + "ProjectName").FirstOrDefault()
                     ?? doc.Descendants(ns + "RootNamespace").FirstOrDefault();
            var name = el?.Value?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? NewestSourceUtc(string projectDir)
    {
        DateTime? newest = null;
        foreach (var pattern in new[] { "*.h", "*.hpp", "*.cpp", "*.cxx", "*.cc" })
        {
            foreach (var file in EnumerateSourceFiles(projectDir, pattern))
            {
                var mtime = File.GetLastWriteTimeUtc(file);
                if (newest is null || mtime > newest.Value)
                {
                    newest = mtime;
                }
            }
        }
        return newest;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectDir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(projectDir, pattern, SearchOption.AllDirectories)
                .Where(p => !IsInBuildOutput(p, projectDir));
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static bool IsInBuildOutput(string filePath, string projectDir)
    {
        var relative = Path.GetRelativePath(projectDir, filePath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var seg in segments)
        {
            if (seg.Equals("x64", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("obj", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("Debug", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("Release", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public sealed record CppShimResolutionResult(
    string? DllPath,
    bool IsFresh,
    DateTime DllUtc,
    DateTime? NewestSourceUtc)
{
    public static readonly CppShimResolutionResult None =
        new(DllPath: null, IsFresh: false, DllUtc: DateTime.MinValue, NewestSourceUtc: null);

    public bool HasDll => DllPath is not null;
}
