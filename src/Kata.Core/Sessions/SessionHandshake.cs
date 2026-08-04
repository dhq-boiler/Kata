using System.Text.Json;

namespace Kata.Core.Sessions;

public static class SessionHandshake
{
    public static string HandshakeFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kata",
        "active-session.json");

    public static void Publish(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("Solution path must be non-empty", nameof(solutionPath));
        }

        var absolutePath = Path.GetFullPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(HandshakeFilePath)!);
        var payload = new HandshakePayload(absolutePath, DateTimeOffset.UtcNow, Environment.ProcessId);
        var json = JsonSerializer.Serialize(payload, HandshakeJsonContext.Default.HandshakePayload);
        File.WriteAllText(HandshakeFilePath, json);
    }

    public static HandshakePayload? TryRead()
    {
        if (!File.Exists(HandshakeFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(HandshakeFilePath);
            var payload = JsonSerializer.Deserialize(json, HandshakeJsonContext.Default.HandshakePayload);
            return payload is null || !File.Exists(payload.SolutionPath) ? null : payload;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public static void Clear()
    {
        if (File.Exists(HandshakeFilePath))
        {
            try { File.Delete(HandshakeFilePath); } catch (IOException) { }
        }
    }
}

public sealed record HandshakePayload(string SolutionPath, DateTimeOffset UpdatedAt, int PublisherProcessId);

[System.Text.Json.Serialization.JsonSerializable(typeof(HandshakePayload))]
internal partial class HandshakeJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
