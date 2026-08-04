using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterGuardClausesTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterGuardClausesTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-gc-" + Guid.NewGuid().ToString("N"));
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
    public async Task Then_is_guard_strips_else_and_hoists_body()
    {
        var source =
            """
            namespace MyLib;

            public class Payroll
            {
                public decimal Amount(bool isDead, decimal salary)
                {
                    if (isDead)
                    {
                        return 0m;
                    }
                    else
                    {
                        var bonus = salary * 0.1m;
                        return salary + bonus;
                    }
                }
            }
            """;
        var (adapter, model, payroll, path) = await SetupAsync(source, "Payroll");
        using var _ = adapter;

        var amount = payroll.Members.Single(m => m.Name == "Amount");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (isDead)", System.StringComparison.Ordinal);

        var intent = new ReplaceNestedConditionalWithGuardClausesIntent
        {
            Source = IntentSource.Human,
            OwnerType = payroll.Ref,
            ContainingMember = amount.Ref,
            SelectionStart = start,
            SelectionLength = 1,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        // Guard remains as-is.
        Assert.Contains("if (isDead)", text);
        Assert.Contains("return 0m;", text);
        // Else is gone.
        Assert.DoesNotContain("else", text);
        // Hoisted body is at method level.
        Assert.Contains("var bonus = salary * 0.1m;", text);
        Assert.Contains("return salary + bonus;", text);
    }

    [Fact]
    public async Task Else_is_guard_inverts_condition_and_hoists_then()
    {
        var source =
            """
            namespace MyLib;

            public class Cfg
            {
                public int Read(bool ready)
                {
                    if (ready)
                    {
                        var v = 42;
                        return v;
                    }
                    else
                    {
                        return -1;
                    }
                }
            }
            """;
        var (adapter, model, cfg, path) = await SetupAsync(source, "Cfg");
        using var _ = adapter;

        var read = cfg.Members.Single(m => m.Name == "Read");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (ready)", System.StringComparison.Ordinal);

        var intent = new ReplaceNestedConditionalWithGuardClausesIntent
        {
            Source = IntentSource.Human,
            OwnerType = cfg.Ref,
            ContainingMember = read.Ref,
            SelectionStart = start,
            SelectionLength = 1,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        // Condition inverted, else-return became the guard.
        // ready is a plain identifier — inversion wraps in `!(...)`.
        Assert.True(text.Contains("if (!(ready))") || text.Contains("if (!ready)"),
            $"expected inverted guard, got:\n{text}");
        Assert.Contains("return -1;", text);
        // Original then-body hoisted.
        Assert.Contains("var v = 42;", text);
        Assert.Contains("return v;", text);
    }
}
