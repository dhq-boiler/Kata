using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceTypeCodeWithClassTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceTypeCodeWithClassTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rtc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Creates_type_class_with_static_instances_and_changes_field_type()
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

            public class Person
            {
                public int Gender = 0;
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
        var gender = person.Members.Single(m => m.Name == "Gender");

        var intent = new ReplaceTypeCodeWithClassIntent
        {
            Source = IntentSource.Human,
            OwnerType = person.Ref,
            Field = gender.Ref,
            NewClassName = "GenderCode",
            Codes = new[]
            {
                new TypeCodeEntry("Male", "0"),
                new TypeCodeEntry("Female", "1"),
                new TypeCodeEntry("Other", "2"),
            },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var personChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Person.cs");
        Assert.Contains("public GenderCode Gender", personChange.NewText!);

        var newClass = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("GenderCode.cs", newClass.FilePath);
        var text = newClass.NewText!;
        Assert.Contains("public sealed class GenderCode", text);
        Assert.Contains("public static readonly GenderCode Male = new(0);", text);
        Assert.Contains("public static readonly GenderCode Female = new(1);", text);
        Assert.Contains("public static readonly GenderCode Other = new(2);", text);
        Assert.Contains("public int Code { get; }", text);
        Assert.Contains("private GenderCode(int code)", text);
    }
}
