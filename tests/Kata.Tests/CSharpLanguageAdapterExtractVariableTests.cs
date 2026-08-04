using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterExtractVariableTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterExtractVariableTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-ev-" + Guid.NewGuid().ToString("N"));
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
    public async Task Lifts_selected_expression_into_local()
    {
        var source =
            """
            namespace MyLib;

            public class Calc
            {
                public int Compute(int a, int b)
                {
                    return (a + b) * 2;
                }
            }
            """;
        var (adapter, model, calc, path) = await SetupAsync(source, "Calc");
        using var _ = adapter;

        var compute = calc.Members.Single(m => m.Name == "Compute");
        var onDisk = File.ReadAllText(path);
        var start = onDisk.IndexOf("(a + b)", System.StringComparison.Ordinal);
        var len = "(a + b)".Length;

        var intent = new ExtractVariableIntent
        {
            Source = IntentSource.Human,
            OwnerType = calc.Ref,
            ContainingMember = compute.Ref,
            SelectionStart = start,
            SelectionLength = len,
            NewVariableName = "sum",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = changeSet.Changes.Single().NewText!;

        Assert.Contains("var sum = (a + b);", text);
        Assert.Contains("return sum * 2;", text);
    }
}
