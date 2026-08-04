using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterTeaseApartInheritanceTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterTeaseApartInheritanceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-tap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private async Task<(CSharpLanguageAdapter, SolutionModel, TypeModel)> SetupAsync(string classSource, string typeName)
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
        await File.WriteAllTextAsync(Path.Combine(projDir, $"{typeName}.cs"), classSource);

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
    public async Task Scaffolds_secondary_hierarchy_and_delegation_field()
    {
        var (adapter, model, deal) = await SetupAsync(
            """
            namespace MyLib;

            public abstract class Deal
            {
                public string Ticker { get; init; } = "";
            }
            """, "Deal");
        using var _ = adapter;

        var intent = new TeaseApartInheritanceIntent
        {
            Source = IntentSource.Human,
            PrimaryHierarchyRoot = deal.Ref,
            SecondaryHierarchyName = "DealSide",
            SecondarySubclassNames = new[] { "Bid", "Ask" },
            DelegationFieldName = "_side",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        // Delegation field is inserted at the top of the primary class.
        var dealChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Deal.cs");
        Assert.Contains("protected DealSide? _side;", dealChange.NewText!);

        // Secondary root is abstract and empty.
        var dealSide = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "DealSide.cs");
        Assert.Equal(DocumentChangeKind.Added, dealSide.Kind);
        Assert.Contains("public abstract class DealSide", dealSide.NewText!);

        // Each subclass extends the new secondary root.
        foreach (var name in new[] { "Bid", "Ask" })
        {
            var sub = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == $"{name}.cs");
            Assert.Equal(DocumentChangeKind.Added, sub.Kind);
            Assert.Contains($"public class {name} : DealSide", sub.NewText!);
        }
    }

    [Fact]
    public async Task Skips_delegation_field_when_already_declared()
    {
        var (adapter, model, deal) = await SetupAsync(
            """
            namespace MyLib;

            public class Deal
            {
                protected DealSide? _side;
                public string Ticker { get; init; } = "";
            }

            public abstract class DealSide { }
            """, "Deal");
        using var _ = adapter;

        var intent = new TeaseApartInheritanceIntent
        {
            Source = IntentSource.Human,
            PrimaryHierarchyRoot = deal.Ref,
            SecondaryHierarchyName = "DealSide",
            SecondarySubclassNames = new[] { "Bid" },
            DelegationFieldName = "_side",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        // Deal.cs should NOT change — field already there.
        Assert.DoesNotContain(changeSet.Changes, c =>
            Path.GetFileName(c.FilePath) == "Deal.cs" && c.Kind == DocumentChangeKind.Modified);

        // Secondary root file gets emitted regardless (adapter doesn't check
        // whether the target namespace already has it — user can decline the diff).
        Assert.Contains(changeSet.Changes, c => Path.GetFileName(c.FilePath) == "DealSide.cs");
        Assert.Contains(changeSet.Changes, c => Path.GetFileName(c.FilePath) == "Bid.cs");
    }
}
