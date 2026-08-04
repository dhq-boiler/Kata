using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterExtractClassTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterExtractClassTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-extract-class-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Extract_class_moves_members_and_adds_delegate_property()
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

        var srcPath = Path.Combine(projDir, "Person.cs");
        await File.WriteAllTextAsync(srcPath,
            """
            namespace MyLib;

            public class Person
            {
                public string Name { get; set; } = string.Empty;
                public string OfficeAreaCode { get; set; } = string.Empty;
                public string OfficeNumber { get; set; } = string.Empty;
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
        var areaCode = person.Members.Single(m => m.Name == "OfficeAreaCode");
        var number = person.Members.Single(m => m.Name == "OfficeNumber");

        var intent = new ExtractClassIntent
        {
            Source = IntentSource.Human,
            SourceType = person.Ref,
            Members = new[] { areaCode.Ref, number.Ref },
            ProposedClassName = "TelephoneNumber",
            DelegatePropertyName = "Telephone",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        Assert.Equal(2, changeSet.Changes.Count);
        var added = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        var modified = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Modified);

        Assert.EndsWith("TelephoneNumber.cs", added.FilePath);
        Assert.Contains("public class TelephoneNumber", added.NewText!);
        Assert.Contains("public string OfficeAreaCode", added.NewText!);
        Assert.Contains("public string OfficeNumber", added.NewText!);
        // Extract Class does NOT produce an abstract class.
        Assert.DoesNotContain("abstract", added.NewText!);

        Assert.EndsWith("Person.cs", modified.FilePath);
        Assert.Contains("public TelephoneNumber Telephone", modified.NewText!);
        Assert.Contains("new TelephoneNumber()", modified.NewText!);
        Assert.DoesNotContain("OfficeAreaCode", modified.NewText!);
        Assert.DoesNotContain("OfficeNumber", modified.NewText!);
        Assert.Contains("public string Name", modified.NewText!);
        // Extract Class does NOT touch the base list (delegation, not inheritance).
        Assert.DoesNotContain("class Person :", modified.NewText!);

        await adapter.ApplyChangesAsync(changeSet);
        var reloaded = await adapter.LoadSolutionAsync(slnxPath);
        Assert.Contains(reloaded.Projects.Single().Types, t => t.Name == "TelephoneNumber" && t.Kind == TypeKind.Class);
    }
}
