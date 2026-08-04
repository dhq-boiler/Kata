using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterMoveMethodTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterMoveMethodTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-movem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Move_method_transfers_method_between_unrelated_classes()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Cart.cs"),
            """
            namespace MyLib;

            public class Cart
            {
                public decimal FormatTotal() => 0m;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "Formatter.cs"),
            """
            namespace MyLib;

            public class Formatter
            {
                public string Currency = "USD";
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

        var cart = model.Projects.Single().Types.Single(t => t.Name == "Cart");
        var formatTotal = cart.Members.Single(m => m.Name == "FormatTotal");

        var intent = new MoveMethodIntent
        {
            Source = IntentSource.Human,
            SourceType = cart.Ref,
            TargetType = new TypeRef("MyLib.Formatter"),
            Members = new[] { formatTotal.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var cartChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Cart.cs");
        Assert.DoesNotContain("FormatTotal", cartChange.NewText!);

        var formatterChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Formatter.cs");
        Assert.Contains("public decimal FormatTotal()", formatterChange.NewText!);
        Assert.Contains("public string Currency", formatterChange.NewText!);
    }
}
