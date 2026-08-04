using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceTypeCodeWithSubclassesTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceTypeCodeWithSubclassesTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Makes_parent_abstract_and_creates_subclass_files()
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
                public int Type = 0;
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

        var intent = new ReplaceTypeCodeWithSubclassesIntent
        {
            Source = IntentSource.Human,
            OwnerType = employee.Ref,
            SubclassNames = new[] { "Engineer", "Manager", "Salesman" },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        Assert.Contains("public abstract class Employee", employeeChange.NewText!);

        Assert.Contains(changeSet.Changes, c => Path.GetFileName(c.FilePath) == "Engineer.cs" && c.Kind == DocumentChangeKind.Added);
        Assert.Contains(changeSet.Changes, c => Path.GetFileName(c.FilePath) == "Manager.cs" && c.Kind == DocumentChangeKind.Added);
        Assert.Contains(changeSet.Changes, c => Path.GetFileName(c.FilePath) == "Salesman.cs" && c.Kind == DocumentChangeKind.Added);

        var engineer = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Engineer.cs");
        Assert.Contains("public class Engineer : Employee", engineer.NewText!);
    }
}
