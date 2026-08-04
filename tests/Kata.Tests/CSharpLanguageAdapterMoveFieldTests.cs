using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterMoveFieldTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterMoveFieldTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-movef-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Move_field_transfers_field_between_unrelated_classes()
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
                public string Name = "unnamed";
                public decimal SalaryAmount = 0m;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "Compensation.cs"),
            """
            namespace MyLib;

            public class Compensation
            {
                public string PayFrequency = "monthly";
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

        var employee = model.Projects.Single().Types.Single(t => t.Name == "Employee");
        var salary = employee.Members.Single(m => m.Name == "SalaryAmount");

        var intent = new MoveFieldIntent
        {
            Source = IntentSource.Human,
            SourceType = employee.Ref,
            TargetType = new TypeRef("MyLib.Compensation"),
            Members = new[] { salary.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.DoesNotContain("SalaryAmount", employeeChange.NewText!);
        Assert.Contains("public string Name", employeeChange.NewText!);

        var compensationChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Compensation.cs");
        Assert.Contains("public decimal SalaryAmount", compensationChange.NewText!);
        Assert.Contains("public string PayFrequency", compensationChange.NewText!);
    }
}
