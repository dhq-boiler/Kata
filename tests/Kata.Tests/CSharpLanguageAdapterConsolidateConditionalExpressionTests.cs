using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterConsolidateConditionalExpressionTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterConsolidateConditionalExpressionTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-cce-" + Guid.NewGuid().ToString("N"));
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
    public async Task Merges_three_consecutive_ifs_with_identical_body()
    {
        var source =
            """
            namespace MyLib;

            public class Payroll
            {
                public decimal DisabilityAmount(bool isDead, bool isSeparated, bool isRetired, decimal real)
                {
                    if (isDead) return 0m;
                    if (isSeparated) return 0m;
                    if (isRetired) return 0m;
                    return real;
                }
            }
            """;
        var (adapter, model, payroll, path) = await SetupAsync(source, "Payroll");
        using var _ = adapter;

        var da = payroll.Members.Single(m => m.Name == "DisabilityAmount");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (isDead)", System.StringComparison.Ordinal);
        var endMarker = "if (isRetired) return 0m;";
        var endIdx = onDisk.IndexOf(endMarker, System.StringComparison.Ordinal) + endMarker.Length;

        var intent = new ConsolidateConditionalExpressionIntent
        {
            Source = IntentSource.Human,
            OwnerType = payroll.Ref,
            ContainingMember = da.Ref,
            SelectionStart = start,
            SelectionLength = endIdx - start,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        Assert.Contains("if (isDead || isSeparated || isRetired)", text);
        Assert.Contains("return 0m;", text);
        // Original individual ifs are gone.
        Assert.DoesNotContain("if (isSeparated) return", text);
    }

    [Fact]
    public async Task Rejects_when_bodies_differ()
    {
        var source =
            """
            namespace MyLib;

            public class Guard
            {
                public int Check(int a, int b)
                {
                    if (a < 0) return 1;
                    if (b < 0) return 2;
                    return 0;
                }
            }
            """;
        var (adapter, model, guard, path) = await SetupAsync(source, "Guard");
        using var _ = adapter;

        var check = guard.Members.Single(m => m.Name == "Check");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("if (a < 0)", System.StringComparison.Ordinal);
        var endMarker = "if (b < 0) return 2;";
        var endIdx = onDisk.IndexOf(endMarker, System.StringComparison.Ordinal) + endMarker.Length;

        var intent = new ConsolidateConditionalExpressionIntent
        {
            Source = IntentSource.Human,
            OwnerType = guard.Ref,
            ContainingMember = check.Ref,
            SelectionStart = start,
            SelectionLength = endIdx - start,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ProposeChangesAsync(model, new[] { intent }));
    }
}
