using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterEncapsulateFieldTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterEncapsulateFieldTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-encap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Converts_public_field_to_auto_property_preserving_name_and_type()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Order.cs"),
            """
            namespace MyLib;

            public class Order
            {
                public decimal Total = 0m;
                public string CustomerName = string.Empty;
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

        var order = model.Projects.Single().Types.Single(t => t.Name == "Order");
        var total = order.Members.Single(m => m.Name == "Total");

        var intent = new EncapsulateFieldIntent
        {
            Source = IntentSource.Human,
            OwnerType = order.Ref,
            Field = total.Ref,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var orderChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Order.cs");
        var text = orderChange.NewText!;
        Assert.Contains("public decimal Total { get; set; } = 0m;", text);
        Assert.Contains("public string CustomerName = string.Empty;", text); // untouched
    }
}
