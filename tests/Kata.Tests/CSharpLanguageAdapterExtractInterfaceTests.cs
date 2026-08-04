using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterExtractInterfaceTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterExtractInterfaceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Extracts_interface_and_wires_class_base_list()
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

        var intent = new ExtractInterfaceIntent
        {
            Source = IntentSource.Human,
            SourceType = greeter.Ref,
            Members = new[] { helloMember.Ref, countMember.Ref },
            ProposedInterfaceName = "IGreeter",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        Assert.Equal(2, changeSet.Changes.Count);
        var addedChange = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        var modifiedChange = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Modified);

        Assert.EndsWith("IGreeter.cs", addedChange.FilePath);
        Assert.Contains("public interface IGreeter", addedChange.NewText!);
        Assert.Contains("string Hello(string name);", addedChange.NewText!);
        Assert.Contains("int Count { get; set; }", addedChange.NewText!);

        Assert.Contains("Greeter.cs", modifiedChange.FilePath);
        Assert.Contains("class Greeter : IGreeter", modifiedChange.NewText!);

        await adapter.ApplyChangesAsync(changeSet);

        var interfacePath = Path.Combine(projDir, "IGreeter.cs");
        Assert.True(File.Exists(interfacePath));
        var writtenInterface = await File.ReadAllTextAsync(interfacePath);
        Assert.Contains("public interface IGreeter", writtenInterface);

        var writtenGreeter = await File.ReadAllTextAsync(srcPath);
        Assert.Contains("class Greeter : IGreeter", writtenGreeter);

        var reloaded = await adapter.LoadSolutionAsync(slnxPath);
        var reloadedGreeter = reloaded.Projects.Single().Types.Single(t => t.Name == "Greeter");
        Assert.Contains(reloadedGreeter.ImplementedInterfaces, tr => tr.FullyQualifiedName == "MyLib.IGreeter");
        Assert.Contains(reloaded.Projects.Single().Types, t => t.Name == "IGreeter" && t.Kind == TypeKind.Interface);
    }

    [Fact]
    public async Task Extract_emits_usings_for_external_namespace_types()
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

        var srcPath = Path.Combine(projDir, "Repository.cs");
        await File.WriteAllTextAsync(srcPath,
            """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace MyLib;

            public class Repository
            {
                public Task<Dictionary<string, int>> LoadCountsAsync() => Task.FromResult(new Dictionary<string, int>());
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
        var repository = model.Projects.Single().Types.Single(t => t.Name == "Repository");
        var loadMember = repository.Members.Single(m => m.Name == "LoadCountsAsync");

        var intent = new ExtractInterfaceIntent
        {
            Source = IntentSource.Human,
            SourceType = repository.Ref,
            Members = new[] { loadMember.Ref },
            ProposedInterfaceName = "IRepository",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var addedChange = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);

        Assert.Contains("using System.Collections.Generic;", addedChange.NewText!);
        Assert.Contains("using System.Threading.Tasks;", addedChange.NewText!);
        Assert.Contains("Task<Dictionary<string, int>> LoadCountsAsync();", addedChange.NewText!);
        Assert.DoesNotContain("using MyLib;", addedChange.NewText!);
    }
}
