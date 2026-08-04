using Kata.Core.Sln;

namespace Kata.Tests;

public sealed class SolutionProjectDiscoveryTests : IDisposable
{
    private readonly string _sandbox;

    public SolutionProjectDiscoveryTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void Slnx_finds_vcxproj_and_skips_csproj()
    {
        var slnx = Path.Combine(_sandbox, "Mix.slnx");
        File.WriteAllText(slnx,
            """
            <Solution>
              <Project Path="Managed/Managed.csproj" />
              <Project Path="Native/Native.vcxproj" />
            </Solution>
            """);

        var discovered = SolutionProjectDiscovery.DiscoverForeignProjects(
            slnx,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vcxproj" });

        var single = Assert.Single(discovered);
        Assert.Equal("Native", single.Name);
        Assert.Equal(".vcxproj", single.Extension);
        Assert.EndsWith(Path.Combine("Native", "Native.vcxproj"), single.AbsolutePath);
    }

    [Fact]
    public void Sln_line_format_finds_vcxproj()
    {
        var sln = Path.Combine(_sandbox, "Mix.sln");
        File.WriteAllText(sln,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Managed", "Managed\Managed.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "Native", "Native\Native.vcxproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var discovered = SolutionProjectDiscovery.DiscoverForeignProjects(
            sln,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vcxproj" });

        var single = Assert.Single(discovered);
        Assert.Equal("Native", single.Name);
        Assert.Equal(".vcxproj", single.Extension);
    }

    [Fact]
    public void Empty_when_no_matching_extension()
    {
        var slnx = Path.Combine(_sandbox, "OnlyManaged.slnx");
        File.WriteAllText(slnx,
            """
            <Solution>
              <Project Path="A/A.csproj" />
              <Project Path="B/B.csproj" />
            </Solution>
            """);

        var discovered = SolutionProjectDiscovery.DiscoverForeignProjects(
            slnx,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vcxproj" });

        Assert.Empty(discovered);
    }
}
