using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceSubclassWithFieldsTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceSubclassWithFieldsTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rsf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Un_abstracts_parent_and_deletes_named_subclass_files()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Person.cs"),
            """
            namespace MyLib;

            public abstract class Person
            {
                public string Name = string.Empty;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "Male.cs"),
            """
            namespace MyLib;

            public class Male : Person
            {
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "Female.cs"),
            """
            namespace MyLib;

            public class Female : Person
            {
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
        var person = model.Projects.Single().Types.Single(t => t.Name == "Person");

        var intent = new ReplaceSubclassWithFieldsIntent
        {
            Source = IntentSource.Human,
            ParentType = person.Ref,
            SubclassesToRemove = new[]
            {
                new TypeRef("MyLib.Male"),
                new TypeRef("MyLib.Female"),
            },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var personChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Person.cs");
        Assert.DoesNotContain("abstract", personChange.NewText!);
        Assert.Contains("public class Person", personChange.NewText!);

        var maleChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Male.cs");
        Assert.Equal(DocumentChangeKind.Deleted, maleChange.Kind);
        var femaleChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Female.cs");
        Assert.Equal(DocumentChangeKind.Deleted, femaleChange.Kind);
    }
}
