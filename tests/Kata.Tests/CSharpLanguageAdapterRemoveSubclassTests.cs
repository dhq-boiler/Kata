using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterRemoveSubclassTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterRemoveSubclassTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-remove-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Remove_subclass_deletes_file_and_rewrites_usages()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Shape.cs"),
            """
            namespace MyLib;

            public class Shape
            {
                public string Kind => "shape";
            }
            """);

        var circlePath = Path.Combine(projDir, "Circle.cs");
        await File.WriteAllTextAsync(circlePath,
            """
            namespace MyLib;

            public class Circle : Shape
            {
                public double Radius { get; set; }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "Registry.cs"),
            """
            namespace MyLib;

            public class Registry
            {
                public Circle MakeCircle() => new Circle();
                public Circle? LastCircle;
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

        var intent = new RemoveSubclassIntent
        {
            Source = IntentSource.Human,
            Subclass = new TypeRef("MyLib.Circle"),
            ReplacementBase = new TypeRef("MyLib.Shape"),
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        // Registry.cs modified — "Circle" replaced with "Shape".
        var registryChange = changeSet.Changes.Single(c => c.FilePath.EndsWith("Registry.cs", StringComparison.Ordinal));
        Assert.Equal(DocumentChangeKind.Modified, registryChange.Kind);
        Assert.Contains("public Shape MakeCircle()", registryChange.NewText!);
        Assert.Contains("new Shape()", registryChange.NewText!);
        Assert.Contains("Shape? LastCircle", registryChange.NewText!);
        // Whole-word only: "MakeCircle" / "LastCircle" keep their internal "Circle".
        Assert.DoesNotContain("public Circle", registryChange.NewText!);
        Assert.DoesNotContain("new Circle(", registryChange.NewText!);

        // Circle.cs is scheduled for deletion.
        var circleChange = changeSet.Changes.Single(c => c.FilePath.EndsWith("Circle.cs", StringComparison.Ordinal));
        Assert.Equal(DocumentChangeKind.Deleted, circleChange.Kind);

        await adapter.ApplyChangesAsync(changeSet);
        Assert.False(File.Exists(circlePath), "Circle.cs should be gone after apply.");
        var registryAfter = await File.ReadAllTextAsync(Path.Combine(projDir, "Registry.cs"));
        Assert.Contains("new Shape()", registryAfter);
    }
}
