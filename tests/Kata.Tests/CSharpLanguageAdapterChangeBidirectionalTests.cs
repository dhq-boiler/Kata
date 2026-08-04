using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterChangeBidirectionalTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterChangeBidirectionalTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-unidir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Removes_named_field_leaving_other_members_intact()
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
                public string Id = string.Empty;
                public Customer Owner = null!;
            }

            public class Customer
            {
                public string Name = string.Empty;
                public Order LastOrder = null!;
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
        var owner = order.Members.Single(m => m.Name == "Owner");

        var intent = new ChangeBidirectionalToUnidirectionalIntent
        {
            Source = IntentSource.Human,
            OwnerType = order.Ref,
            Field = owner.Ref,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var orderChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Order.cs");
        var text = orderChange.NewText!;
        Assert.DoesNotContain("public Customer Owner", text);
        Assert.Contains("public string Id", text);
        // Customer side untouched — still has LastOrder back-reference which is the point:
        // we intentionally removed only ONE direction.
        Assert.Contains("public Order LastOrder", text);
    }
}
