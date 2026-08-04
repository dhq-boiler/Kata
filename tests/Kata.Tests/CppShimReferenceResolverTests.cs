using Kata.Cpp.Bridge;

namespace Kata.Tests;

public sealed class CppShimReferenceResolverTests : IDisposable
{
    private readonly string _root;

    public CppShimReferenceResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kata-shim-ref-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Returns_null_when_vcxproj_does_not_exist()
    {
        var result = CppShimReferenceResolver.TryResolveFreshDll(Path.Combine(_root, "missing.vcxproj"));
        Assert.Null(result);
    }

    [Fact]
    public void ResolveDll_returns_stale_flagged_dll_when_source_is_newer()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 1);
        var sourceFile = Directory.EnumerateFiles(Path.Combine(_root, "Sample"), "*.h").First();
        var sourceUtc = File.GetLastWriteTimeUtc(sourceFile);
        var dll = CreateDll("Sample", "Debug", "Sample", ageSeconds: 0);
        File.SetLastWriteTimeUtc(dll, sourceUtc.AddHours(-1)); // stale by 1 h

        var res = CppShimReferenceResolver.ResolveDll(vcx);

        Assert.True(res.HasDll);
        Assert.False(res.IsFresh);
        Assert.Equal(dll, res.DllPath);
    }

    [Fact]
    public void ResolveDll_prefers_fresh_dll_when_both_present()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 1);
        var freshDll = CreateDll("Sample", "Debug", "Sample", ageSeconds: -60); // future — fresh
        var staleDll = CreateDll("Sample", "Release", "Sample", ageSeconds: 0);
        var sourceFile = Directory.EnumerateFiles(Path.Combine(_root, "Sample"), "*.h").First();
        var sourceUtc = File.GetLastWriteTimeUtc(sourceFile);
        File.SetLastWriteTimeUtc(staleDll, sourceUtc.AddHours(-1));

        var res = CppShimReferenceResolver.ResolveDll(vcx);

        Assert.True(res.IsFresh);
        Assert.Equal(freshDll, res.DllPath);
    }

    [Fact]
    public void Returns_null_when_no_dll_present()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 1);
        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_dll_when_fresher_than_all_sources()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 3);
        var dll = CreateDll("Sample", "Debug", "Sample", ageSeconds: -30); // 30 seconds in the future vs sources

        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);

        Assert.Equal(dll, result);
    }

    [Fact]
    public void Returns_null_when_dll_older_than_any_source()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 2);
        // Set one source to be newer than the DLL by 1 hour
        var newSource = Path.Combine(_root, "Sample", "NewHeader.h");
        File.WriteAllText(newSource, "");
        File.SetLastWriteTimeUtc(newSource, DateTime.UtcNow.AddHours(1));
        CreateDll("Sample", "Debug", "Sample", ageSeconds: 0);

        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);

        Assert.Null(result);
    }

    [Fact]
    public void Picks_newest_fresh_dll_among_multiple_configs()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 1);
        CreateDll("Sample", "Debug", "Sample", ageSeconds: -60);
        var releaseDll = CreateDll("Sample", "Release", "Sample", ageSeconds: -120); // newest
        CreateDll("Sample", "Release(NoObfuscated)", "Sample", ageSeconds: -30);      // fresh but older

        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);

        Assert.Equal(releaseDll, result);
    }

    [Fact]
    public void Skips_stale_dll_and_falls_back_to_fresh_one()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 1);
        // Release DLL predates the source (stale); Debug DLL is fresh
        var sourceFile = Directory.EnumerateFiles(Path.Combine(_root, "Sample"), "*.h").First();
        var sourceUtc = File.GetLastWriteTimeUtc(sourceFile);
        var debugDll = CreateDll("Sample", "Debug", "Sample", ageSeconds: -30);
        var releaseDll = CreateDll("Sample", "Release", "Sample", ageSeconds: 0);
        File.SetLastWriteTimeUtc(releaseDll, sourceUtc.AddDays(-1));

        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);

        Assert.Equal(debugDll, result);
    }

    [Fact]
    public void Uses_ProjectName_element_when_present()
    {
        var vcx = CreateProject(name: "SampleDir", withSourceCount: 1, projectNameOverride: "PrettyName");
        var dll = CreateDll("SampleDir", "Debug", "PrettyName", ageSeconds: -30);

        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);

        Assert.Equal(dll, result);
    }

    [Fact]
    public void Ignores_sources_under_build_output_directories()
    {
        var vcx = CreateProject(name: "Sample", withSourceCount: 1);
        // Simulate a generated .cpp under obj/ that is newer than the DLL
        var objDir = Path.Combine(_root, "Sample", "obj", "Debug");
        Directory.CreateDirectory(objDir);
        var genFile = Path.Combine(objDir, "generated.cpp");
        File.WriteAllText(genFile, "");
        File.SetLastWriteTimeUtc(genFile, DateTime.UtcNow.AddHours(1));

        var dll = CreateDll("Sample", "Debug", "Sample", ageSeconds: -30);

        var result = CppShimReferenceResolver.TryResolveFreshDll(vcx);

        Assert.Equal(dll, result);
    }

    private string CreateProject(string name, int withSourceCount, string? projectNameOverride = null)
    {
        var projDir = Path.Combine(_root, name);
        Directory.CreateDirectory(projDir);
        var vcxPath = Path.Combine(projDir, name + ".vcxproj");

        var projectNameElement = projectNameOverride is null
            ? string.Empty
            : $"<ProjectName>{projectNameOverride}</ProjectName>";

        File.WriteAllText(vcxPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup Label="Globals">
                {projectNameElement}
              </PropertyGroup>
            </Project>
            """);

        var sourceBaseUtc = DateTime.UtcNow.AddMinutes(-5);
        for (int i = 0; i < withSourceCount; i++)
        {
            var srcPath = Path.Combine(projDir, $"Header{i}.h");
            File.WriteAllText(srcPath, $"// header {i}");
            File.SetLastWriteTimeUtc(srcPath, sourceBaseUtc);
        }
        return vcxPath;
    }

    private string CreateDll(string projectDirName, string config, string dllName, double ageSeconds)
    {
        var dllDir = Path.Combine(_root, projectDirName, "x64", config);
        Directory.CreateDirectory(dllDir);
        var dllPath = Path.Combine(dllDir, dllName + ".dll");
        File.WriteAllText(dllPath, "MZ");
        File.SetLastWriteTimeUtc(dllPath, DateTime.UtcNow.AddSeconds(-ageSeconds));
        return dllPath;
    }
}
