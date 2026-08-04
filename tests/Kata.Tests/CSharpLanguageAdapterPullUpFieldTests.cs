using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterPullUpFieldTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterPullUpFieldTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-pullup-field-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Pull_up_field_moves_field_from_subclass_to_parent()
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
                public string Name = string.Empty;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "SalariedEmployee.cs"),
            """
            namespace MyLib;

            public class SalariedEmployee : Employee
            {
                public decimal AnnualSalary;
                public decimal Bonus() => AnnualSalary * 0.10m;
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
        var salaryField = sub.Members.Single(m => m.Name == "AnnualSalary");
        var intent = new PullUpFieldIntent
        {
            Source = IntentSource.Human,
            Subclass = sub.Ref,
            Parent = new TypeRef("MyLib.Employee"),
            Members = new[] { salaryField.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.Contains("public decimal AnnualSalary", employeeChange.NewText!);
        Assert.Contains("public string Name", employeeChange.NewText!);

        var salariedChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.cs");
        // Field declaration removed from sub, but references to it (Bonus body) remain.
        var newSub = salariedChange.NewText!;
        Assert.DoesNotContain("public decimal AnnualSalary;", newSub);
        Assert.Contains("Bonus()", newSub);
    }
}
