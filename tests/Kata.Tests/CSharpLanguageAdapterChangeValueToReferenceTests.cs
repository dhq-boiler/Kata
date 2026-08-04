using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterChangeValueToReferenceTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterChangeValueToReferenceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-cvr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Adds_registry_dictionary_and_get_or_create_factory()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Customer.cs"),
            """
            namespace MyLib;

            public class Customer
            {
                public string Name = string.Empty;
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
        var customer = model.Projects.Single().Types.Single(t => t.Name == "Customer");

        var intent = new ChangeValueToReferenceIntent
        {
            Source = IntentSource.Human,
            OwnerType = customer.Ref,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;

        Assert.Contains("private static readonly", text);
        Assert.Contains("Dictionary<string, Customer>", text);
        Assert.Contains("_instances", text);
        Assert.Contains("public static Customer GetOrCreate(string key", text);
        Assert.Contains("_instances.TryGetValue(key, out var value)", text);
    }
}
