using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterInlineMethodTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterInlineMethodTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-im-" + Guid.NewGuid().ToString("N"));
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
    public async Task Inlines_expression_bodied_method_at_call_site()
    {
        var source =
            """
            namespace MyLib;

            public class Calc
            {
                public int Times2(int n) => n * 2;
                public int Go(int x) { return Times2(x + 1); }
            }
            """;
        var (adapter, model, calc, path) = await SetupAsync(source, "Calc");
        using var _ = adapter;

        var go = calc.Members.Single(m => m.Name == "Go");
        var onDisk = File.ReadAllText(path);
        // Selection points at the invocation `Times2(x + 1)` inside Go.
        var start = onDisk.IndexOf("Times2(x + 1)", System.StringComparison.Ordinal);
        var len = "Times2(x + 1)".Length;

        var intent = new InlineMethodIntent
        {
            Source = IntentSource.Human,
            OwnerType = calc.Ref,
            ContainingMember = go.Ref,
            SelectionStart = start,
            SelectionLength = len,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;
        // Body of Times2 substituted with arg (x + 1) at the call site.
        // Result: `return ((x + 1) * 2);`
        Assert.Contains("return ((x + 1) * 2);", text);
        // Declaration is intentionally left in place — user deletes when all callers inlined.
        Assert.Contains("public int Times2(int n) => n * 2;", text);
    }

    [Fact]
    public async Task Rejects_block_body_with_more_than_one_return_statement()
    {
        var source =
            """
            namespace MyLib;

            public class Calc
            {
                public int Fancy(int n)
                {
                    var t = n + 1;
                    return t * 2;
                }
                public int Go() { return Fancy(3); }
            }
            """;
        var (adapter, model, calc, path) = await SetupAsync(source, "Calc");
        using var _ = adapter;

        var go = calc.Members.Single(m => m.Name == "Go");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("Fancy(3)", System.StringComparison.Ordinal);
        var len = "Fancy(3)".Length;

        var intent = new InlineMethodIntent
        {
            Source = IntentSource.Human,
            OwnerType = calc.Ref,
            ContainingMember = go.Ref,
            SelectionStart = start,
            SelectionLength = len,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ProposeChangesAsync(model, new[] { intent }));
    }
}
