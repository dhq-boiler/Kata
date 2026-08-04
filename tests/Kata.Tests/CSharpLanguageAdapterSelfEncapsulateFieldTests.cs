using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterSelfEncapsulateFieldTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterSelfEncapsulateFieldTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-sef-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Adds_property_and_rewrites_internal_field_accesses_to_property()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Counter.cs"),
            """
            namespace MyLib;

            public class Counter
            {
                private int count = 0;

                public int Read() => count;
                public void Bump() { count = count + 1; }
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
        var counter = model.Projects.Single().Types.Single(t => t.Name == "Counter");
        var count = counter.Members.Single(m => m.Name == "count");

        var intent = new SelfEncapsulateFieldIntent
        {
            Source = IntentSource.Human,
            OwnerType = counter.Ref,
            Field = count.Ref,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;

        // Field stays. Property added with same-name accessors reading/writing the field.
        Assert.Contains("private int count", text);
        Assert.Contains("public int Count", text);
        Assert.Contains("get => count", text);
        Assert.Contains("set => count = value", text);

        // Bodies now go through the property (not the field).
        Assert.Contains("Read() => Count", text);
        Assert.Contains("Count = Count + 1", text);
    }
}
