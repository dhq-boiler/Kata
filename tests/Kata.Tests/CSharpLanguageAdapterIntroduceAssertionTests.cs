using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterIntroduceAssertionTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterIntroduceAssertionTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-ia-" + Guid.NewGuid().ToString("N"));
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
    public async Task Inserts_debug_assert_at_top_of_method_body()
    {
        var source =
            """
            namespace MyLib;

            public class Bank
            {
                public decimal Withdraw(decimal amount)
                {
                    return amount * 1.05m;
                }
            }
            """;
        var (adapter, model, bank, path) = await SetupAsync(source, "Bank");
        using var _ = adapter;

        var withdraw = bank.Members.Single(m => m.Name == "Withdraw");
        var onDisk = File.ReadAllText(path);
        var caret = onDisk.IndexOf("return amount", System.StringComparison.Ordinal);

        var intent = new IntroduceAssertionIntent
        {
            Source = IntentSource.Human,
            OwnerType = bank.Ref,
            ContainingMember = withdraw.Ref,
            SelectionStart = caret,
            AssertionExpression = "amount > 0",
            Message = "amount must be positive",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        Assert.Contains("System.Diagnostics.Debug.Assert(amount > 0, \"amount must be positive\");", text);
        // Assertion is above the return.
        var assertIdx = text.IndexOf("Debug.Assert", System.StringComparison.Ordinal);
        var retIdx = text.IndexOf("return amount", System.StringComparison.Ordinal);
        Assert.True(assertIdx < retIdx);
    }

    [Fact]
    public async Task Inserts_at_top_of_nested_block_when_caret_is_inside_it()
    {
        var source =
            """
            namespace MyLib;

            public class Runner
            {
                public void Go(int n)
                {
                    if (n > 0)
                    {
                        var x = n * 2;
                    }
                }
            }
            """;
        var (adapter, model, runner, path) = await SetupAsync(source, "Runner");
        using var _ = adapter;

        var go = runner.Members.Single(m => m.Name == "Go");
        var onDisk = File.ReadAllText(path);
        var caret = onDisk.IndexOf("var x =", System.StringComparison.Ordinal);

        var intent = new IntroduceAssertionIntent
        {
            Source = IntentSource.Human,
            OwnerType = runner.Ref,
            ContainingMember = go.Ref,
            SelectionStart = caret,
            AssertionExpression = "n < 1000",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;
        // Assertion goes inside the if-block, above the var declaration.
        Assert.Contains("System.Diagnostics.Debug.Assert(n < 1000", text);
        var assertIdx = text.IndexOf("Debug.Assert", System.StringComparison.Ordinal);
        var varIdx = text.IndexOf("var x =", System.StringComparison.Ordinal);
        Assert.True(assertIdx < varIdx);
    }
}
