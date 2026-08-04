using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterRenameFieldTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterRenameFieldTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rename-field-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Rename_field_renames_declaration_and_usages()
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
                public decimal totalAmount;
                public decimal ComputeDiscount() => totalAmount * 0.1m;
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
        var totalField = order.Members.Single(m => m.Name == "totalAmount");

        var intent = new RenameFieldIntent
        {
            Source = IntentSource.Human,
            OwnerType = order.Ref,
            Field = totalField.Ref,
            NewName = "TotalAmount",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var orderChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Order.cs");
        Assert.Contains("public decimal TotalAmount", orderChange.NewText!);
        Assert.Contains("TotalAmount * 0.1m", orderChange.NewText!);
        Assert.DoesNotContain("totalAmount", orderChange.NewText!);
    }
}
