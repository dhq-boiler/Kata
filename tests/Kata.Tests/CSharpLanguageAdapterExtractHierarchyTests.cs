using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterExtractHierarchyTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterExtractHierarchyTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-eh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private async Task<(CSharpLanguageAdapter, SolutionModel, TypeModel)> SetupAsync(string classSource, string typeName)
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
        await File.WriteAllTextAsync(Path.Combine(projDir, $"{typeName}.cs"), classSource);

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
        return (adapter, model, t);
    }

    [Fact]
    public async Task Without_methods_to_virtualize_behaves_like_replace_type_code_with_subclasses()
    {
        var (adapter, model, employee) = await SetupAsync(
            """
            namespace MyLib;

            public class Employee
            {
                public int Salary { get; set; }
            }
            """, "Employee");
        using var _ = adapter;

        var intent = new ExtractHierarchyIntent
        {
            Source = IntentSource.Human,
            OwnerType = employee.Ref,
            SubclassNames = new[] { "Engineer", "Manager" },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.Contains("public abstract class Employee", employeeChange.NewText!);

        var engineer = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Engineer.cs");
        Assert.Equal(DocumentChangeKind.Added, engineer.Kind);
        Assert.Contains("public class Engineer : Employee", engineer.NewText!);
        // No override stubs when nothing was virtualized.
        Assert.DoesNotContain("override", engineer.NewText!);
    }

    [Fact]
    public async Task Virtualizes_selected_methods_and_stubs_them_in_each_subclass()
    {
        var (adapter, model, shape) = await SetupAsync(
            """
            namespace MyLib;

            public class Shape
            {
                public double Area()
                {
                    return 0;
                }

                public string Describe(string prefix)
                {
                    return prefix + "shape";
                }

                public int Sides => 0;
            }
            """, "Shape");
        using var _ = adapter;

        var area = shape.Members.Single(m => m.Name == "Area");
        var describe = shape.Members.Single(m => m.Name == "Describe");

        var intent = new ExtractHierarchyIntent
        {
            Source = IntentSource.Human,
            OwnerType = shape.Ref,
            SubclassNames = new[] { "Circle", "Square" },
            MethodsToVirtualize = new[] { area.Ref, describe.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        // Owner is now abstract and both methods are declared abstract with no body.
        var shapeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Shape.cs");
        var shapeText = shapeChange.NewText!;
        Assert.Contains("public abstract class Shape", shapeText);
        Assert.Contains("public abstract double Area();", shapeText);
        Assert.Contains("public abstract string Describe(string prefix);", shapeText);
        // Non-virtualized member untouched.
        Assert.Contains("public int Sides => 0;", shapeText);

        // Each subclass overrides both methods with NotImplementedException stubs.
        foreach (var name in new[] { "Circle", "Square" })
        {
            var sub = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == $"{name}.cs");
            Assert.Equal(DocumentChangeKind.Added, sub.Kind);
            Assert.Contains($"public class {name} : Shape", sub.NewText!);
            Assert.Contains("public override double Area()", sub.NewText!);
            Assert.Contains("public override string Describe(string prefix)", sub.NewText!);
            Assert.Contains("throw new System.NotImplementedException", sub.NewText!);
        }
    }

    [Fact]
    public async Task Silently_ignores_methods_not_found_on_owner()
    {
        var (adapter, model, shape) = await SetupAsync(
            """
            namespace MyLib;

            public class Shape
            {
                public double Area() => 0;
            }
            """, "Shape");
        using var _ = adapter;

        var area = shape.Members.Single(m => m.Name == "Area");
        var bogus = new MemberRef(shape.Ref, "DoesNotExist()");
        var intent = new ExtractHierarchyIntent
        {
            Source = IntentSource.Human,
            OwnerType = shape.Ref,
            SubclassNames = new[] { "Circle" },
            MethodsToVirtualize = new[] { area.Ref, bogus },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var circle = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Circle.cs");
        Assert.Contains("public override double Area()", circle.NewText!);
        Assert.DoesNotContain("DoesNotExist", circle.NewText!);
    }
}
