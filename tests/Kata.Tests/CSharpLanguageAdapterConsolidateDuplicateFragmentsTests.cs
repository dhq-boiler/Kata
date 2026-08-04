using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterConsolidateDuplicateFragmentsTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterConsolidateDuplicateFragmentsTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-cdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private async Task<(CSharpLanguageAdapter, SolutionModel, TypeModel, string)> SetupAsync(string source, string typeName)
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
        var filePath = Path.Combine(projDir, $"{typeName}.cs");
        await File.WriteAllTextAsync(filePath, source);

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
        return (adapter, model, t, filePath);
    }

    [Fact]
    public async Task Hoists_common_suffix_out_of_both_branches()
    {
        var source =
            """
            namespace MyLib;

            public class Deal
            {
                public void Process(bool isSpecial, decimal price)
                {
                    decimal total;
                    if (isSpecial)
                    {
                        total = price * 0.95m;
                        Send();
                    }
                    else
                    {
                        total = price * 0.98m;
                        Send();
                    }
                }
                void Send() { }
            }
            """;
        var (adapter, model, deal, path) = await SetupAsync(source, "Deal");
        using var _ = adapter;

        var process = deal.Members.Single(m => m.Name == "Process");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (isSpecial)", System.StringComparison.Ordinal);

        var intent = new ConsolidateDuplicateConditionalFragmentsIntent
        {
            Source = IntentSource.Human,
            OwnerType = deal.Ref,
            ContainingMember = process.Ref,
            SelectionStart = start,
            SelectionLength = 1,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        // Both branches keep only their own assignment.
        Assert.Contains("total = price * 0.95m;", text);
        Assert.Contains("total = price * 0.98m;", text);
        // Send() is now called exactly ONCE, outside the if.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"Send\(\);"));
    }

    [Fact]
    public async Task Refuses_if_without_else()
    {
        var source =
            """
            namespace MyLib;

            public class Foo
            {
                public void Bar(bool flag)
                {
                    if (flag)
                    {
                        System.Console.WriteLine("hi");
                    }
                }
            }
            """;
        var (adapter, model, foo, path) = await SetupAsync(source, "Foo");
        using var _ = adapter;

        var bar = foo.Members.Single(m => m.Name == "Bar");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (flag)", System.StringComparison.Ordinal);

        var intent = new ConsolidateDuplicateConditionalFragmentsIntent
        {
            Source = IntentSource.Human,
            OwnerType = foo.Ref,
            ContainingMember = bar.Ref,
            SelectionStart = start,
            SelectionLength = 1,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ProposeChangesAsync(model, new[] { intent }));
    }
}
