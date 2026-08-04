using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceConstructorWithFactoryTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceConstructorWithFactoryTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Adds_static_factory_and_makes_constructor_private()
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
                public Order(string id, decimal total)
                {
                    Id = id;
                    Total = total;
                }

                public string Id { get; }
                public decimal Total { get; }
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

        var intent = new ReplaceConstructorWithFactoryIntent
        {
            Source = IntentSource.Human,
            OwnerType = order.Ref,
            FactoryName = "Create",
            MakeConstructorPrivate = true,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var orderChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Order.cs");
        var text = orderChange.NewText!;
        Assert.Contains("public static Order Create(string id, decimal total)", text);
        Assert.Contains("=> new Order(id, total)", text);
        Assert.Contains("private Order(string id, decimal total)", text);
    }
}
