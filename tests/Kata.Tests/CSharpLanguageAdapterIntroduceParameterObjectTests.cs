using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Roslyn;

namespace Kata.Tests;

public sealed class CSharpLanguageAdapterIntroduceParameterObjectTests : IDisposable
{
    private readonly string _sandbox;

    public CSharpLanguageAdapterIntroduceParameterObjectTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-ipo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task Creates_parameter_object_and_rewrites_method_signature_and_body()
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

        await File.WriteAllTextAsync(Path.Combine(projDir, "Booking.cs"),
            """
            namespace MyLib;

            public class Booking
            {
                public string Describe(string flight, int seat, string cabin)
                {
                    return $"{flight}/{cabin}#{seat}";
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
        var booking = model.Projects.Single().Types.Single(t => t.Name == "Booking");
        var describe = booking.Members.Single(m => m.Name == "Describe");

        var intent = new IntroduceParameterObjectIntent
        {
            Source = IntentSource.Human,
            OwnerType = booking.Ref,
            Method = describe.Ref,
            ProposedObjectName = "BookingRequest",
            ParameterName = "req",
        };

        var changeSet = await adapter.ProposeChangesAsync(model, new[] { intent });

        var bookingChange = changeSet.Changes.Single(c => Path.GetFileName(c.FilePath) == "Booking.cs");
        var text = bookingChange.NewText!;
        Assert.Contains("public string Describe(BookingRequest req)", text);
        Assert.Contains("$\"{req.flight}/{req.cabin}#{req.seat}\"", text);

        var added = changeSet.Changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("BookingRequest.cs", added.FilePath);
        var poText = added.NewText!;
        Assert.Contains("public class BookingRequest", poText);
        Assert.Contains("public string flight;", poText);
        Assert.Contains("public int seat;", poText);
        Assert.Contains("public string cabin;", poText);
    }
}
