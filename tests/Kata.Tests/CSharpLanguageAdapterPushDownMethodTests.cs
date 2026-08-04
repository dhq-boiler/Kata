using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterPushDownMethodTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterPushDownMethodTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-pushdown-method-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Push_down_method_moves_method_from_parent_to_subclass()
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
                public decimal QuotaBonus() => 100m;
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(projDir, "SalariedEmployee.cs"),
            """
            namespace MyLib;

            public class SalariedEmployee : Employee
            {
                public decimal Salary { get; set; }
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
        var quotaBonus = parent.Members.Single(m => m.Name == "QuotaBonus");
        var intent = new PushDownMethodIntent
        {
            Source = IntentSource.Human,
            Parent = parent.Ref,
            Subclass = new TypeRef("MyLib.SalariedEmployee"),
            Members = new[] { quotaBonus.Ref },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.DoesNotContain("QuotaBonus", employeeChange.NewText!);
        Assert.Contains("public string Name", employeeChange.NewText!);

        var salariedChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.cs");
        Assert.Contains("public decimal QuotaBonus()", salariedChange.NewText!);
        Assert.Contains("public decimal Salary", salariedChange.NewText!);
    }
}
