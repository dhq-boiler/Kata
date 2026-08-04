using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterIntroduceNullObjectTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterIntroduceNullObjectTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-ino-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private async Task<(CSharpLanguageAdapter, SolutionModel, TypeModel)> SetupAsync(string source, string typeName)
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
        await File.WriteAllTextAsync(Path.Combine(projDir, $"{typeName}.cs"), source);

        var slnxPath = Path.Combine(_sandbox, "Sandbox.slnx");
        await File.WriteAllTextAsync(slnxPath,
            """
            <Solution>
              <Project Path="MyLib/MyLib.csproj" />
            </Solution>
            """);

        var adapter = new CSharpLanguageAdapter();
        var model = await adapter.LoadSolutionAsync(slnxPath);
        var t = model.Projects.Single().Types.Single(t => t.Name == typeName);
        return (adapter, model, t);
    }

    [Fact]
    public async Task Emits_null_object_subclass_with_override_stubs()
    {
        var source =
            """
            namespace MyLib;

            public abstract class Customer
            {
                public abstract string Name { get; }
                public abstract void Charge(decimal amount);
                public abstract decimal Balance();
            }
            """;
        var (adapter, model, customer) = await SetupAsync(source, "Customer");
        using var _ = adapter;

        var intent = new IntroduceNullObjectIntent
        {
            Source = IntentSource.Human,
            SourceType = customer.Ref,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var nullObj = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "NullCustomer.cs");
        Assert.Equal(DocumentChangeKind.Added, nullObj.Kind);
        var text = nullObj.NewText!;

        Assert.Contains("public sealed class NullCustomer : Customer", text);
        Assert.Contains("public static readonly NullCustomer Instance", text);
        Assert.Contains("public override void Charge(decimal amount)", text);
        Assert.Contains("// no-op", text);
        Assert.Contains("public override decimal Balance()", text);
        Assert.Contains("return default(decimal);", text);
    }

    [Fact]
    public async Task Rejects_sealed_non_abstract_source_type()
    {
        var source =
            """
            namespace MyLib;

            public sealed class FinalThing
            {
                public void Do() { }
            }
            """;
        var (adapter, model, finalThing) = await SetupAsync(source, "FinalThing");
        using var _ = adapter;

        var intent = new IntroduceNullObjectIntent
        {
            Source = IntentSource.Human,
            SourceType = finalThing.Ref,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ProposeChangesAsync(model, new[] { intent }));
    }
}
