using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceArrayWithObjectTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceArrayWithObjectTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rao-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Creates_row_class_with_named_fields_and_changes_array_field_type()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Roster.cs"),
            """
            namespace MyLib;

            public class Roster
            {
                public string[] Row = new string[3];
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
        var roster = model.Projects.Single().Types.Single(t => t.Name == "Roster");
        var row = roster.Members.Single(m => m.Name == "Row");

        var intent = new ReplaceArrayWithObjectIntent
        {
            Source = IntentSource.Human,
            OwnerType = roster.Ref,
            ArrayField = row.Ref,
            NewClassName = "RosterRow",
            FieldMappings = new[]
            {
                new ArrayFieldMapping(0, "Name", "string"),
                new ArrayFieldMapping(1, "Rank", "string"),
                new ArrayFieldMapping(2, "Squad", "string"),
            },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var rosterChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Roster.cs");
        Assert.Contains("public RosterRow Row", rosterChange.NewText!);
        Assert.DoesNotContain("string[]", rosterChange.NewText!);

        var newClass = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("RosterRow.cs", newClass.FilePath);
        var text = newClass.NewText!;
        Assert.Contains("public class RosterRow", text);
        Assert.Contains("public string Name { get; set; }", text);
        Assert.Contains("public string Rank { get; set; }", text);
        Assert.Contains("public string Squad { get; set; }", text);
        Assert.Contains("Was array index 0", text);
    }
}
