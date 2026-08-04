using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterExtractMethodTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterExtractMethodTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-em-" + Guid.NewGuid().ToString("N"));
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

    // Selects text from the first occurrence of startMarker through (and
    // including) the end of the line containing endMarker. Line-ending
    // agnostic — walks forward until '\n' or EOF after endMarker's index.
    private static (int Start, int Length) FindRange(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"Start marker '{startMarker}' not found.");
        var endIdx = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (endIdx < 0) throw new InvalidOperationException($"End marker '{endMarker}' not found after start.");
        var endLine = source.IndexOf('\n', endIdx);
        int lineEnd = endLine < 0 ? source.Length : endLine + 1;
        return (start, lineEnd - start);
    }

    [Fact]
    public async Task Extracts_selected_statements_into_new_void_method()
    {
        var source =
            """
            namespace MyLib;

            public class Widget
            {
                public void Render()
                {
                    var w = 10;
                    var h = 20;
                    System.Console.WriteLine($"{w}x{h}");
                }
            }
            """;
        var (adapter, model, widget, path) = await SetupAsync(source, "Widget");
        using var _ = adapter;

        var render = widget.Members.Single(m => m.Name == "Render");
        var onDisk = File.ReadAllText(path);
        var (start, len) = FindRange(onDisk, "var w = 10;", "System.Console.WriteLine");

        var intent = new ExtractMethodIntent
        {
            Source = IntentSource.Human,
            OwnerType = widget.Ref,
            ContainingMember = render.Ref,
            SelectionStart = start,
            SelectionLength = len,
            NewMethodName = "DoRender",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        // Original method now calls the extracted one.
        Assert.Contains("public void Render()", text);
        Assert.Contains("DoRender();", text);
        // New method contains the statements.
        Assert.Contains("private void DoRender()", text);
        Assert.Contains("var w = 10;", text);
        Assert.Contains("var h = 20;", text);
    }

    [Fact]
    public async Task Infers_parameters_from_variables_read_from_outer_scope()
    {
        var source =
            """
            namespace MyLib;

            public class Calc
            {
                public int Sum(int a, int b)
                {
                    var seed = 100;
                    var result = a + b + seed;
                    return result;
                }
            }
            """;
        var (adapter, model, calc, path) = await SetupAsync(source, "Calc");
        using var _ = adapter;

        var sum = calc.Members.Single(m => m.Name == "Sum");
        var onDisk = File.ReadAllText(path);
        var (start, len) = FindRange(onDisk, "var result = a + b + seed;", "var result = a + b + seed;");

        var intent = new ExtractMethodIntent
        {
            Source = IntentSource.Human,
            OwnerType = calc.Ref,
            ContainingMember = sum.Ref,
            SelectionStart = start,
            SelectionLength = len,
            NewMethodName = "Compute",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        // Params include the three variables that flow in (a, b, seed).
        // `result` is DECLARED inside the selection, so it's the return.
        Assert.Contains("private int Compute(int a, int b, int seed)", text);
        Assert.Contains("return result;", text);
        // Caller now assigns from the call.
        Assert.Contains("var result = Compute(a, b, seed);", text);
    }

    [Fact]
    public async Task Rejects_control_flow_escape_selections()
    {
        var source =
            """
            namespace MyLib;

            public class Runner
            {
                public int Go(int n)
                {
                    if (n < 0)
                    {
                        return -1;
                    }
                    return n * 2;
                }
            }
            """;
        var (adapter, model, runner, path) = await SetupAsync(source, "Runner");
        using var _ = adapter;

        var go = runner.Members.Single(m => m.Name == "Go");
        var onDisk = File.ReadAllText(path);
        // From `if (n < 0)` through the closing `}` of that block.
        var (start, len) = FindRange(onDisk, "if (n < 0)", "return -1;");
        // Extend to include the closing brace of the if block.
        var closingBrace = onDisk.IndexOf('}', start + len);
        if (closingBrace >= 0) len = closingBrace - start + 1;

        var intent = new ExtractMethodIntent
        {
            Source = IntentSource.Human,
            OwnerType = runner.Ref,
            ContainingMember = go.Ref,
            SelectionStart = start,
            SelectionLength = len,
            NewMethodName = "GuardNegative",
        };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.ProposeChangesAsync(model, new[] { intent }));
    }
}
