using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterPushDownFieldTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterPushDownFieldTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-pushdown-field-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Push_down_field_moves_field_from_parent_to_subclass()
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
                public decimal SalesQuota;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "SalariedEmployee.cs"),
            """
            namespace MyLib;

            public class SalariedEmployee : Employee
            {
                public decimal AnnualSalary;
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

        var parent = model.Projects.Single().Types.Single(t => t.Name == "Employee");
        var quotaField = parent.Members.Single(m => m.Name == "SalesQuota");
        var intent = new PushDownFieldIntent
        {
            Source = IntentSource.Human,
            Parent = parent.Ref,
            Subclass = new TypeRef("MyLib.SalariedEmployee"),
            Members = new[] { quotaField.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.DoesNotContain("SalesQuota", employeeChange.NewText!);
        Assert.Contains("public string Name", employeeChange.NewText!);

        var salariedChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.cs");
        Assert.Contains("public decimal SalesQuota", salariedChange.NewText!);
        Assert.Contains("public decimal AnnualSalary", salariedChange.NewText!);
    }
}
