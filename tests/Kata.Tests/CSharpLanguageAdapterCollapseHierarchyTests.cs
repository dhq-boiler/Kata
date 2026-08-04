using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterCollapseHierarchyTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterCollapseHierarchyTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-collapse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Collapse_moves_members_to_parent_and_deletes_subclass()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Employee.cs"),
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Payroll.cs"),
            """
            namespace MyLib;

            public class Payroll
            {
                public SalariedEmployee CreateStaff() => new SalariedEmployee();
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

        var intent = new CollapseHierarchyIntent
        {
            Source = IntentSource.Human,
            Subclass = new TypeRef("MyLib.SalariedEmployee"),
            Parent = new TypeRef("MyLib.Employee"),
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.Equal(DocumentChangeKind.Modified, employeeChange.Kind);
        // Members moved up
        Assert.Contains("public decimal Salary", employeeChange.NewText!);
        Assert.Contains("public decimal Bonus()", employeeChange.NewText!);
        // Original member preserved
        Assert.Contains("public string Name", employeeChange.NewText!);

        var salariedChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.cs");
        Assert.Equal(DocumentChangeKind.Deleted, salariedChange.Kind);

        var payrollChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Payroll.cs");
        Assert.Equal(DocumentChangeKind.Modified, payrollChange.Kind);
        Assert.Contains("public Employee CreateStaff()", payrollChange.NewText!);
        Assert.Contains("new Employee()", payrollChange.NewText!);

        await adapter.ApplyChangesAsync(changeSet);
        Assert.False(File.Exists(salariedPath), "SalariedEmployee.cs should be gone after apply.");
    }
}
