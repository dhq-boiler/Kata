using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterAddRemoveParameterTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterAddRemoveParameterTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-param-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private async Task<(CSharpLanguageAdapter, SolutionModel, TypeModel)> SetupAsync(string classSource, string typeName)
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

        await File.WriteAllTextAsync(Path.Combine(projDir, $"{typeName}.cs"), classSource);

        var slnxPath = Path.Combine(_sandbox, "Sandbox.slnx");
        await File.WriteAllTextAsync(slnxPath,
            """
            <Solution>
              <Project Path="MyLib/MyLib.csproj" />
            </Solution>
            """);

        var adapter = new CSharpLanguageAdapter();
        var model = await adapter.LoadSolutionAsync(slnxPath);
        var t = model.Projects.Single().Types.Single(t => t.Name == typeName);
        return (adapter, model, t);
    }

    [Fact]
    public async Task Add_parameter_appends_to_method_signature_with_default()
    {
        var (adapter, model, greeter) = await SetupAsync(
            """
            namespace MyLib;

            public class Greeter
            {
                public string Say(string name) => $"hi {name}";
            }
            """, "Greeter");
        using var _ = adapter;

        var say = greeter.Members.Single(m => m.Name == "Say");
        var intent = new AddParameterIntent
        {
            Source = IntentSource.Human,
            OwnerType = greeter.Ref,
            Method = say.Ref,
            ParameterType = "int",
            ParameterName = "times",
            DefaultValue = "1",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;
        Assert.Contains("public string Say(string name, int times = 1)", text);
    }

    [Fact]
    public async Task Remove_parameter_drops_named_argument_from_signature()
    {
        var (adapter, model, greeter) = await SetupAsync(
            """
            namespace MyLib;

            public class Greeter
            {
                public string Say(string name, int times, bool loud) => $"hi {name}";
            }
            """, "Greeter");
        using var _ = adapter;

        var say = greeter.Members.Single(m => m.Name == "Say");
        var intent = new RemoveParameterIntent
        {
            Source = IntentSource.Human,
            OwnerType = greeter.Ref,
            Method = say.Ref,
            ParameterName = "times",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var change = changeSet.Changes.Single();
        var text = change.NewText!;
        Assert.Contains("public string Say(string name, bool loud)", text);
        Assert.DoesNotContain("times", text);
    }

    [Fact]
    public async Task Add_parameter_inserts_default_at_callers_when_no_default_supplied()
    {
        var (adapter, model, greeter) = await SetupAsync(
            """
            namespace MyLib;

            public class Greeter
            {
                public string Say(string name) => $"hi {name}";
            }

            public class Caller
            {
                public void Go() { new Greeter().Say("world"); }
            }
            """, "Greeter");
        using var _ = adapter;

        var say = greeter.Members.Single(m => m.Name == "Say");
        var intent = new AddParameterIntent
        {
            Source = IntentSource.Human,
            OwnerType = greeter.Ref,
            Method = say.Ref,
            ParameterType = "int",
            ParameterName = "times",
            DefaultValue = string.Empty,
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var joined = string.Join("\n\n=====\n\n", changeSet.Changes.Select(c => $"[{c.FilePath}]\n{c.NewText}"));
        Assert.True(changeSet.Changes.Count >= 1, $"expected >=1 changes, got {changeSet.Changes.Count}. Content:\n{joined}");
        var text = string.Concat(changeSet.Changes.Select(c => c.NewText));
        Assert.Contains("public string Say(string name, int times)", text);
        Assert.True(text.Contains("Say(\"world\", default)"), $"Caller not rewritten. Changes:\n{joined}");
    }

    [Fact]
    public async Task Remove_parameter_drops_argument_at_callers()
    {
        var (adapter, model, greeter) = await SetupAsync(
            """
            namespace MyLib;

            public class Greeter
            {
                public string Say(string name, int times, bool loud) => $"hi {name}";
            }

            public class Caller
            {
                public void Go() { new Greeter().Say("world", 3, false); }
            }
            """, "Greeter");
        using var _ = adapter;

        var say = greeter.Members.Single(m => m.Name == "Say");
        var intent = new RemoveParameterIntent
        {
            Source = IntentSource.Human,
            OwnerType = greeter.Ref,
            Method = say.Ref,
            ParameterName = "times",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        var text = string.Concat(changeSet.Changes.Select(c => c.NewText));
        Assert.Contains("public string Say(string name, bool loud)", text);
        Assert.Contains("new Greeter().Say(\"world\", false)", text);
    }

    [Fact]
    public async Task Remove_parameter_drops_named_argument_at_caller_by_name()
    {
        var (adapter, model, greeter) = await SetupAsync(
            """
            namespace MyLib;

            public class Greeter
            {
                public string Say(string name, int times, bool loud) => $"hi {name}";
            }

            public class Caller
            {
                public void Go() { new Greeter().Say(name: "world", loud: false, times: 3); }
            }
            """, "Greeter");
        using var _ = adapter;

        var say = greeter.Members.Single(m => m.Name == "Say");
        var intent = new RemoveParameterIntent
        {
            Source = IntentSource.Human,
            OwnerType = greeter.Ref,
            Method = say.Ref,
            ParameterName = "times",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });
        // The declaration-only change and the callsite change land as separate DocumentChange
        // entries on the same file; pick the one whose NewText no longer contains `times: 3`.
        var callSiteChange = changeSet.Changes.Single(c => !c.NewText!.Contains("times: 3"));
        Assert.Contains("Say(name: \"world\", loud: false)", callSiteChange.NewText!);
    }
}
