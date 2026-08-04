using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterDecomposeConditionalTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterDecomposeConditionalTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-dc-" + Guid.NewGuid().ToString("N"));
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
    public async Task Extracts_condition_and_both_branches_into_methods()
    {
        var source =
            """
            namespace MyLib;

            public class Pricing
            {
                public void ApplyDiscount(int qty, decimal price)
                {
                    if (qty > 100 && price > 50m)
                    {
                        System.Console.WriteLine("bulk");
                    }
                    else
                    {
                        System.Console.WriteLine("normal");
                    }
                }
            }
            """;
        var (adapter, model, pricing, path) = await SetupAsync(source, "Pricing");
        using var _ = adapter;

        var apply = pricing.Members.Single(m => m.Name == "ApplyDiscount");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (qty > 100", System.StringComparison.Ordinal);

        var intent = new DecomposeConditionalIntent
        {
            Source = IntentSource.Human,
            OwnerType = pricing.Ref,
            ContainingMember = apply.Ref,
            SelectionStart = start,
            SelectionLength = 1,
            ConditionMethodName = "IsBulkOrder",
            ThenMethodName = "ApplyBulkDiscount",
            ElseMethodName = "ApplyNormalPrice",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        // Call site is rewritten to invoke the three new methods.
        Assert.Contains("if (IsBulkOrder(qty, price))", text);
        Assert.Contains("ApplyBulkDiscount();", text);
        Assert.Contains("ApplyNormalPrice();", text);
        // The three extracted methods exist.
        Assert.Contains("private bool IsBulkOrder(int qty, decimal price)", text);
        Assert.Contains("private void ApplyBulkDiscount()", text);
        Assert.Contains("private void ApplyNormalPrice()", text);
    }

    [Fact]
    public async Task Skips_else_method_when_if_has_no_else()
    {
        var source =
            """
            namespace MyLib;

            public class Guard
            {
                public void Run(int n)
                {
                    if (n < 0)
                    {
                        System.Console.WriteLine("negative");
                    }
                }
            }
            """;
        var (adapter, model, guard, path) = await SetupAsync(source, "Guard");
        using var _ = adapter;

        var run = guard.Members.Single(m => m.Name == "Run");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (n < 0)", System.StringComparison.Ordinal);

        var intent = new DecomposeConditionalIntent
        {
            Source = IntentSource.Human,
            OwnerType = guard.Ref,
            ContainingMember = run.Ref,
            SelectionStart = start,
            SelectionLength = 1,
            ConditionMethodName = "IsInvalid",
            ThenMethodName = "LogInvalid",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        Assert.Contains("if (IsInvalid(n))", text);
        Assert.Contains("LogInvalid();", text);
        Assert.Contains("private bool IsInvalid(int n)", text);
        Assert.Contains("private void LogInvalid()", text);
    }
}
