using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterRenameParameterTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterRenameParameterTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rn-param-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Renames_parameter_across_signature_and_body()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Greeter.cs"),
            """
            namespace MyLib;

            public class Greeter
            {
                public string Say(string n) => $"hi {n}";
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
        var greeter = model.Projects.Single().Types.Single(t => t.Name == "Greeter");
        var say = greeter.Members.Single(m => m.Name == "Say");

        var intent = new RenameParameterIntent
        {
            Source = IntentSource.Human,
            OwnerType = greeter.Ref,
            Method = say.Ref,
            OldName = "n",
            NewName = "name",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;
        Assert.Contains("public string Say(string name)", text);
        Assert.Contains("$\"hi {name}\"", text);
    }
}
