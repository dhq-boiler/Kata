using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterAddGhostTypeTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterAddGhostTypeTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-ghost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Adds_interface_in_nested_namespace_and_creates_folder()
    {
        var projDir = Path.Combine(_sandbox, "MyLib");
        Directory.CreateDirectory(projDir);

        await File.WriteAllTextAsync(Path.Combine(projDir, "MyLib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        var slnxPath = Path.Combine(_sandbox, "Sandbox.slnx");
        await File.WriteAllTextAsync(slnxPath,
            """
            <Solution>
              <Project Path="MyLib/MyLib.csproj" />
            </Solution>
            """);

        using var adapter = new CSharpLanguageAdapter();
        var model = await adapter.LoadSolutionAsync(slnxPath);

        var intent = IntentFactory.AddGhostType(
            proposedName: "IThing",
            @namespace: new NamespaceRef("MyLib.Contracts"),
            kind: TypeKind.Interface,
            source: IntentSource.Human,
            rationale: "Sketch a contract before implementation.");

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        Assert.Single(changeSet.Changes);
        var change = changeSet.Changes[0];
        Assert.Equal(DocumentChangeKind.Added, change.Kind);
        Assert.Contains("public interface IThing", change.NewText!);
        Assert.EndsWith(Path.Combine("MyLib", "Contracts", "IThing.cs"), change.FilePath);

        await adapter.ApplyChangesAsync(changeSet);

        var expectedPath = Path.Combine(projDir, "Contracts", "IThing.cs");
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}");

        var reloaded = await adapter.LoadSolutionAsync(slnxPath);
        var iThing = reloaded.Projects.Single().Types.Single(t => t.Name == "IThing");
        Assert.Equal(TypeKind.Interface, iThing.Kind);
        Assert.Equal("MyLib.Contracts", iThing.Namespace.FullName);
    }

    [Fact]
    public async Task Adds_enum_in_root_namespace()
    {
        var projDir = Path.Combine(_sandbox, "MyLib");
        Directory.CreateDirectory(projDir);

        await File.WriteAllTextAsync(Path.Combine(projDir, "MyLib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var slnxPath = Path.Combine(_sandbox, "Sandbox.slnx");
        await File.WriteAllTextAsync(slnxPath,
            """
            <Solution>
              <Project Path="MyLib/MyLib.csproj" />
            </Solution>
            """);

        using var adapter = new CSharpLanguageAdapter();
        var model = await adapter.LoadSolutionAsync(slnxPath);

        var intent = IntentFactory.AddGhostType(
            proposedName: "Status",
            @namespace: new NamespaceRef("MyLib"),
            kind: TypeKind.Enum,
            source: IntentSource.Ai);

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        Assert.Single(changeSet.Changes);
        Assert.Contains("public enum Status", changeSet.Changes[0].NewText!);

        await adapter.ApplyChangesAsync(changeSet);
        Assert.True(File.Exists(Path.Combine(projDir, "Status.cs")));

        var reloaded = await adapter.LoadSolutionAsync(slnxPath);
        Assert.Contains(reloaded.Projects.Single().Types, t => t.Name == "Status" && t.Kind == TypeKind.Enum);
    }
}
