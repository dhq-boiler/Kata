using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterPullUpConstructorBodyTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterPullUpConstructorBodyTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-pullup-ctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Pull_up_constructor_body_moves_statements_and_adds_base_call()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Manager.cs"),
            """
            namespace MyLib;

            public class Manager : Employee
            {
                public int TeamSize;

                public Manager()
                {
                    Name = "manager";
                    TeamSize = 3;
                }
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

        var intent = new PullUpConstructorBodyIntent
        {
            Source = IntentSource.Human,
            Subclass = new TypeRef("MyLib.Manager"),
            Parent = new TypeRef("MyLib.Employee"),
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var employeeChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.cs");
        // Parent now has a constructor with sub's statements.
        Assert.Contains("public Employee()", employeeChange.NewText!);
        Assert.Contains("Name = \"manager\"", employeeChange.NewText!);
        Assert.Contains("TeamSize = 3", employeeChange.NewText!);

        var managerChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Manager.cs");
        // Sub's constructor delegates to base and has an empty body.
        Assert.Contains(": base()", managerChange.NewText!);
        Assert.DoesNotContain("Name = \"manager\"", managerChange.NewText!);
        Assert.DoesNotContain("TeamSize = 3", managerChange.NewText!);
    }
}
