using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterPullUpMethodTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterPullUpMethodTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-pullup-method-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Pull_up_method_moves_selected_method_from_subclass_to_parent()
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

        var employeePath = Path.Combine(projDir, "Employee.cs");
        await File.WriteAllTextAsync(employeePath,
            """
            namespace MyLib;

            public class Employee
            {
                public string Name { get; set; } = string.Empty;
            }
            """);

        var salariedPath = Path.Combine(projDir, "SalariedEmployee.cs");
        await File.WriteAllTextAsync(salariedPath,
            """
            namespace MyLib;

            public class SalariedEmployee : Employee
            {
                public decimal Salary { get; set; }
                public decimal Bonus() => Salary * 0.10m;
            }
            """);

        var slnxPath = Path.Combine(_sandbox, "Sandbox.slnx");
        await File.WriteAllTextAsync(slnxPath,
            """
            <Solution>
              <Project Path="MyLib/MyLib.csproj" />
            </Solution>
            """);

        using var adapter = new CSharpLanguageAdapter();
        var model = await adapter.LoadSolutionAsync(slnxPath);

        var sub = model.Projects.Single().Types.Single(t => t.Name == "SalariedEmployee");
        var bonus = sub.Members.Single(m => m.Name == "Bonus");
        var intent = new PullUpMethodIntent
        {
            Source = IntentSource.Human,
            Subclass = sub.Ref,
            Parent = new TypeRef("MyLib.Employee"),
            Members = new[] { bonus.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.Equal(DocumentChangeKind.Modified, employeeChange.Kind);
        Assert.Contains("public decimal Bonus()", employeeChange.NewText!);
        Assert.Contains("public string Name", employeeChange.NewText!);

        var salariedChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.cs");
        Assert.Equal(DocumentChangeKind.Modified, salariedChange.Kind);
        Assert.DoesNotContain("Bonus()", salariedChange.NewText!);
        Assert.Contains("public decimal Salary", salariedChange.NewText!);
    }
}
