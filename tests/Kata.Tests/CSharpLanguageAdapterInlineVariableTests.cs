using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterInlineVariableTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterInlineVariableTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-iv-" + Guid.NewGuid().ToString("N"));
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
    public async Task Inlines_single_assignment_local_and_removes_declaration()
    {
        var source =
            """
            namespace MyLib;

            public class Calc
            {
                public int Sum(int a, int b)
                {
                    var seed = a + b;
                    return seed * 2;
                }
            }
            """;
        var (adapter, model, calc, path) = await SetupAsync(source, "Calc");
        using var _ = adapter;

        var sum = calc.Members.Single(m => m.Name == "Sum");
        var onDisk = File.ReadAllText(path);
        // Selection points at the declaration's `seed`.
        var start = onDisk.IndexOf("seed = a + b", System.StringComparison.Ordinal);
        var len = "seed".Length;

        var intent = new InlineVariableIntent
        {
            Source = IntentSource.Human,
            OwnerType = calc.Ref,
            ContainingMember = sum.Ref,
            SelectionStart = start,
            SelectionLength = len,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;
        // Declaration is gone, use site substituted with initializer.
        Assert.DoesNotContain("var seed = ", text);
        Assert.Contains("return (a + b) * 2;", text);
    }

    [Fact]
    public async Task Rejects_reassigned_local()
    {
        var source =
            """
            namespace MyLib;

            public class Calc
            {
                public int Sum(int a, int b)
                {
                    var t = a + b;
                    t = t * 2;
                    return t;
                }
            }
            """;
        var (adapter, model, calc, path) = await SetupAsync(source, "Calc");
        using var _ = adapter;

        var sum = calc.Members.Single(m => m.Name == "Sum");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("t = a + b", System.StringComparison.Ordinal);
        var len = 1;

        var intent = new InlineVariableIntent
        {
            Source = IntentSource.Human,
            OwnerType = calc.Ref,
            ContainingMember = sum.Ref,
            SelectionStart = start,
            SelectionLength = len,
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ProposeChangesAsync(model, new[] { intent }));
    }
}
