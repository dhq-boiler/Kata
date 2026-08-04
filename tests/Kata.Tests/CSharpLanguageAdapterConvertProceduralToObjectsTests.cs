using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterConvertProceduralToObjectsTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterConvertProceduralToObjectsTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-cpo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private async Task<(CSharpLanguageAdapter, SolutionModel)> SetupAsync(string source, string fileName)
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
        await File.WriteAllTextAsync(Path.Combine(projDir, fileName), source);

        var slnxPath = Path.Combine(_sandbox, "Sandbox.slnx");
        await File.WriteAllTextAsync(slnxPath,
            """
            <Solution>
              <Project Path="MyLib/MyLib.csproj" />
            </Solution>
            """);

        var adapter = new CSharpLanguageAdapter();
        var model = await adapter.LoadSolutionAsync(slnxPath);
        return (adapter, model);
    }

    [Fact]
    public async Task Moves_static_method_onto_data_record_and_rewrites_receiver()
    {
        var (adapter, model) = await SetupAsync(
            """
            namespace MyLib;

            public class Order
            {
                public int Qty { get; set; }
                public decimal Price { get; set; }
            }

            public static class OrderProc
            {
                public static decimal Total(Order o)
                {
                    return o.Qty * o.Price;
                }
            }

            public class Caller
            {
                public decimal Go(Order order)
                {
                    return OrderProc.Total(order);
                }
            }
            """, "AllInOne.cs");
        using var _ = adapter;

        var proj = model.Projects.Single();
        var order = proj.Types.Single(t => t.Name == "Order");
        var proc = proj.Types.Single(t => t.Name == "OrderProc");
        var total = proc.Members.Single(m => m.Name == "Total");

        var intent = new ConvertProceduralToObjectsIntent
        {
            Source = IntentSource.Human,
            ProceduralClass = proc.Ref,
            DataRecordType = order.Ref,
            MethodsToMove = new[] { total.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        // All classes were in one file; the change set is a chain of edits on
        // the same file. Pick whichever version has the moved method.
        var final = changeSet.Changes.Last().NewText!;

        // Order now has the instance method.
        Assert.Contains("public decimal Total()", final);
        // Body references got rewritten to `this`.
        Assert.Contains("this.Qty * this.Price", final);
        // OrderProc no longer has Total.
        var procRegion = ExtractClassBody(final, "OrderProc");
        Assert.DoesNotContain("Total", procRegion);
        // Caller now calls `order.Total()` instead of `OrderProc.Total(order)`.
        Assert.Contains("order.Total()", final);
        Assert.DoesNotContain("OrderProc.Total(order)", final);
    }

    [Fact]
    public async Task Silently_skips_methods_whose_first_param_does_not_match_record_type()
    {
        var (adapter, model) = await SetupAsync(
            """
            namespace MyLib;

            public class Order
            {
                public int Qty { get; set; }
            }

            public static class Util
            {
                public static int Twice(int n) => n * 2;
                public static int Qty(Order o) => o.Qty;
            }
            """, "AllInOne.cs");
        using var _ = adapter;

        var proj = model.Projects.Single();
        var order = proj.Types.Single(t => t.Name == "Order");
        var util = proj.Types.Single(t => t.Name == "Util");
        var twice = util.Members.Single(m => m.Name == "Twice");
        var qty = util.Members.Single(m => m.Name == "Qty");

        var intent = new ConvertProceduralToObjectsIntent
        {
            Source = IntentSource.Human,
            ProceduralClass = util.Ref,
            DataRecordType = order.Ref,
            MethodsToMove = new[] { twice.Ref, qty.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var final = changeSet.Changes.Last().NewText!;
        // Qty moved onto Order (first param matched).
        Assert.Contains("this.Qty", final);
        // Twice stayed on Util (first param was int, not Order).
        Assert.Contains("public static int Twice(int n)", final);
    }

    private static string ExtractClassBody(string source, string className)
    {
        var marker = "class " + className;
        var idx = source.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        var openBrace = source.IndexOf('{', idx);
        if (openBrace < 0) return string.Empty;
        int depth = 0;
        for (int i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(openBrace, i - openBrace + 1);
            }
        }
        return source[openBrace..];
    }
}
