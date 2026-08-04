using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterChangeReferenceToValueTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterChangeReferenceToValueTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-crv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Adds_readonly_to_fields_and_replaces_set_with_init_on_properties()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Money.cs"),
            """
            namespace MyLib;

            public class Money
            {
                public decimal Amount;
                public string Currency { get; set; } = "USD";
                public const string DefaultCurrency = "USD";
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
        var money = model.Projects.Single().Types.Single(t => t.Name == "Money");

        var intent = new ChangeReferenceToValueIntent
        {
            Source = IntentSource.Human,
            OwnerType = money.Ref,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;

        Assert.Contains("public readonly decimal Amount", text);
        Assert.Contains("public string Currency { get; init; }", text);
        Assert.Contains("public const string DefaultCurrency", text);
        Assert.DoesNotContain("Currency { get; set; }", text);
    }
}
