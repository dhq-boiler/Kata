using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterGetMemberSourceTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterGetMemberSourceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-getsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Returns_full_method_source_and_body_span_for_a_method()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Calc.cs"),
            """
            namespace MyLib;

            public class Calc
            {
                public int Add(int a, int b)
                {
                    var sum = a + b;
                    return sum;
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

        var calc = model.Projects.Single().Types.Single(t => t.Name == "Calc");
        var addMethod = calc.Members.Single(m => m.Name == "Add");

        var source = await adapter.GetMemberSourceAsync(model, calc.Ref, addMethod.Ref);

        Assert.NotNull(source);
        Assert.Contains("public int Add(int a, int b)", source!.SourceText);
        Assert.Contains("var sum = a + b;", source.SourceText);
        Assert.Contains("return sum;", source.SourceText);
        Assert.EndsWith("Calc.cs", source.FilePath);

        var body = source.SourceText.Substring(source.BodySpanStart, source.BodySpanLength);
        Assert.StartsWith("{", body);
        Assert.EndsWith("}", body);
        Assert.Contains("var sum = a + b;", body);
    }

    [Fact]
    public async Task Returns_null_when_owner_type_missing()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Calc.cs"),
            """
            namespace MyLib;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
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

        var calc = model.Projects.Single().Types.Single(t => t.Name == "Calc");
        var addMethod = calc.Members.Single(m => m.Name == "Add");

        var fakeType = new Kata.Core.Model.TypeRef("MyLib.NoSuchType");
        var source = await adapter.GetMemberSourceAsync(model, fakeType, addMethod.Ref);

        Assert.Null(source);
    }
}
