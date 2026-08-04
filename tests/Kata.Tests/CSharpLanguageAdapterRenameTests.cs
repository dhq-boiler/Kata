using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterRenameTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterRenameTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Renaming_a_type_rewrites_the_source_file()
    {
        var projDir = Path.Combine(_sandbox, "MyLib");
        Directory.CreateDirectory(projDir);

        var csproj = Path.Combine(projDir, "MyLib.csproj");
        await File.WriteAllTextAsync(csproj,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);

        var srcPath = Path.Combine(projDir, "OldName.cs");
        await File.WriteAllTextAsync(srcPath,
            """
            namespace MyLib;

            public class OldName
            {
                public string Greet() => "hi";
            }

            public class Caller
            {
                public OldName MakeIt() => new OldName();
            }
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

        Assert.Contains(model.Projects.Single().Types, t => t.Name == "OldName");

        var rename = new RenameIntent
        {
            Source = IntentSource.Human,
            TargetType = new TypeRef("MyLib.OldName"),
            NewName = "NewName",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { rename });

        Assert.Single(changeSet.Changes);
        var change = changeSet.Changes[0];
        Assert.Contains("class NewName", change.NewText!);
        Assert.Contains("new NewName()", change.NewText!);
        Assert.DoesNotContain("class OldName", change.NewText!);

        await adapter.ApplyChangesAsync(changeSet);

        var updated = await File.ReadAllTextAsync(srcPath);
        Assert.Contains("class NewName", updated);
        Assert.DoesNotContain("class OldName", updated);

        var reloaded = await adapter.LoadSolutionAsync(slnxPath);
        Assert.Contains(reloaded.Projects.Single().Types, t => t.Name == "NewName");
        Assert.DoesNotContain(reloaded.Projects.Single().Types, t => t.Name == "OldName");
    }
}
