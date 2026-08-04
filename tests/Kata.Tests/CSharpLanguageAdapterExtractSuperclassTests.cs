using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterExtractSuperclassTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterExtractSuperclassTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-super-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Extract_superclass_moves_members_and_wires_base_list()
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
              </PropertyGroup>
            </Project>
            """);

        var srcPath = Path.Combine(projDir, "Greeter.cs");
        await File.WriteAllTextAsync(srcPath,
            """
            namespace MyLib;

            public class Greeter
            {
                public string Hello(string name) => $"hi {name}";
                public int Count { get; set; }
                public string LocalOnly() => "keep me";
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
        var greeter = model.Projects.Single().Types.Single(t => t.Name == "Greeter");
        var helloMember = greeter.Members.Single(m => m.Name == "Hello");
        var countMember = greeter.Members.Single(m => m.Name == "Count");

        var intent = new ExtractSuperclassIntent
        {
            Source = IntentSource.Human,
            SourceType = greeter.Ref,
            Members = new[] { helloMember.Ref, countMember.Ref },
            ProposedSuperclassName = "GreeterBase",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        Assert.Equal(2, changeSet.Changes.Count);
        var added = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        var modified = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Modified);

        Assert.EndsWith("GreeterBase.cs", added.FilePath);
        Assert.Contains("public abstract class GreeterBase", added.NewText!);
        Assert.Contains("public string Hello(string name)", added.NewText!);
        Assert.Contains("public int Count", added.NewText!);

        Assert.EndsWith("Greeter.cs", modified.FilePath);
        Assert.Contains("class Greeter : GreeterBase", modified.NewText!);
        // Moved members are gone from the original class body.
        Assert.DoesNotContain("public string Hello", modified.NewText!);
        Assert.DoesNotContain("public int Count", modified.NewText!);
        // Untouched member stays.
        Assert.Contains("LocalOnly", modified.NewText!);

        await adapter.ApplyChangesAsync(changeSet);
        var reloaded = await adapter.LoadSolutionAsync(slnxPath);
        var reloadedGreeter = reloaded.Projects.Single().Types.Single(t => t.Name == "Greeter");
        Assert.Contains(reloadedGreeter.BaseTypes, tr => tr.FullyQualifiedName == "MyLib.GreeterBase");
        Assert.Contains(reloaded.Projects.Single().Types, t => t.Name == "GreeterBase" && t.Kind == TypeKind.Class);
    }
}
