using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterRemoveSettingMethodTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterRemoveSettingMethodTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-remove-setter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Removes_setter_from_property()
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
                public int Id { get; set; }
                public decimal Total { get; set; }
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

        var ownerRef = new TypeRef("MyLib.Order");
        var intent = new RemoveSettingMethodIntent
        {
            Source = IntentSource.Human,
            OwnerType = ownerRef,
            Property = new MemberRef(ownerRef, "Id"),
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var orderChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Order.cs");
        var text = orderChange.NewText!;
        // Id should have only a getter now.
        Assert.Contains("public int Id { get; }", text);
        // Total is untouched, still has both.
        Assert.Contains("public decimal Total { get; set; }", text);
    }
}
