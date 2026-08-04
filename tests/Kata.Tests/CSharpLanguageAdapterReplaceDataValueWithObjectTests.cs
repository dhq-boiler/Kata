using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterReplaceDataValueWithObjectTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterReplaceDataValueWithObjectTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-rdv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Wraps_primitive_field_in_new_class_and_changes_field_type()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Customer.cs"),
            """
            namespace MyLib;

            public class Customer
            {
                public string Email = string.Empty;
                public string Name = string.Empty;
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
        var customer = model.Projects.Single().Types.Single(t => t.Name == "Customer");
        var email = customer.Members.Single(m => m.Name == "Email");

        var intent = new ReplaceDataValueWithObjectIntent
        {
            Source = IntentSource.Human,
            OwnerType = customer.Ref,
            Field = email.Ref,
            WrapperClassName = "EmailAddress",
            InnerFieldName = "Value",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var customerChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Customer.cs");
        Assert.Contains("public EmailAddress Email", customerChange.NewText!);
        Assert.Contains("public string Name", customerChange.NewText!);

        var wrapperChange = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("EmailAddress.cs", wrapperChange.FilePath);
        var wrapText = wrapperChange.NewText!;
        Assert.Contains("public class EmailAddress", wrapText);
        Assert.Contains("public string Value { get; set; }", wrapText);
        Assert.Contains("public EmailAddress(string value)", wrapText);
    }
}
