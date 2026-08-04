using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterPreserveWholeObjectTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterPreserveWholeObjectTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-pwo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Replaces_derived_parameters_with_one_whole_object_and_rewrites_body()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Alarm.cs"),
            """
            namespace MyLib;

            public class Reading { public int low; public int high; }

            public class Alarm
            {
                public bool WithinRange(int low, int high)
                {
                    return low <= 0 && high >= 100;
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
        var alarm = model.Projects.Single().Types.Single(t => t.Name == "Alarm");
        var withinRange = alarm.Members.Single(m => m.Name == "WithinRange");

        var intent = new PreserveWholeObjectIntent
        {
            Source = IntentSource.Human,
            OwnerType = alarm.Ref,
            Method = withinRange.Ref,
            ObjectType = new TypeRef("MyLib.Reading"),
            ParameterName = "reading",
            ReplacedParameterNames = new[] { "low", "high" },
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;

        Assert.Contains("public bool WithinRange(Reading reading)", text);
        Assert.Contains("reading.low <= 0", text);
        Assert.Contains("reading.high >= 100", text);
    }
}
