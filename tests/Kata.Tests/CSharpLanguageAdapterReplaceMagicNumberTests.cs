using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceMagicNumberTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceMagicNumberTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-magic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Adds_private_const_and_replaces_occurrences_within_class()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Circle.cs"),
            """
            namespace MyLib;

            public class Circle
            {
                public double Radius { get; set; }
                public double Circumference() => Radius * 2 * 3.14159;
                public double Diameter() => Radius * 2;
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
        var circle = model.Projects.Single().Types.Single(t => t.Name == "Circle");

        var intent = new ReplaceMagicNumberIntent
        {
            Source = IntentSource.Human,
            OwnerType = circle.Ref,
            LiteralValue = "3.14159",
            ConstantName = "Pi",
            ConstantType = "double",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var circleChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Circle.cs");
        var text = circleChange.NewText!;
        Assert.Contains("private const double Pi = 3.14159;", text);
        Assert.Contains("Radius * 2 * Pi", text);
        // Other numeric literals are untouched.
        Assert.Contains("Radius * 2;", text);
        Assert.DoesNotContain("* 3.14159", text);
    }
}
