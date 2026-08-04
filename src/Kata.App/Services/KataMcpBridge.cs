using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kata.App.Services;

// In-app MCP client that talks to the co-hosted Kata.Mcp Streamable-HTTP server. The AI
// agent (Claude Code / Claude Desktop / …) speaks to the same endpoint as a separate
// client; coordination is via the ai-smell-task queue tools on the server.
//
// Design notes:
// - Endpoint defaults to http://localhost:7345/mcp (matches Program.cs). Override via
//   KATA_MCP_URL env var.
// - Connection is lazy — first call triggers CreateAsync. Reconnect on transport error.
// - IsConnected reflects "we have a live client handle"; not whether an AI agent is
//   listening on the other side (that shows up as tasks never being picked up).
public sealed class KataMcpBridge : IAsyncDisposable
{
    private readonly Uri _endpoint;
    private McpClient? _client;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public KataMcpBridge()
    {
        var url = Environment.GetEnvironmentVariable("KATA_MCP_URL")
                  ?? "http://localhost:7345/mcp";
        _endpoint = new Uri(url);
    }

    public bool IsConnected => _client is not null;

    public async Task<Guid> RequestAiSmellAnalysisAsync(
        string typeFullName,
        string? memberSignature,
        string category,
        string prompt,
        CancellationToken ct)
    {
        var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var args = new Dictionary<string, object?>
        {
            ["typeFullName"] = typeFullName,
            ["memberSignature"] = memberSignature,
            ["category"] = category,
            ["prompt"] = prompt,
        };
        var result = await client
            .CallToolAsync("request_ai_smell_analysis", args, cancellationToken: ct)
            .ConfigureAwait(false);
        var idString = ExtractTextField(result, "taskId")
            ?? throw new InvalidOperationException("request_ai_smell_analysis returned no taskId");
        return Guid.Parse(idString);
    }

    public async Task<AiSmellTaskSnapshot> GetAiSmellTaskAsync(Guid taskId, CancellationToken ct)
    {
        var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var args = new Dictionary<string, object?> { ["taskId"] = taskId.ToString() };
        var result = await client
            .CallToolAsync("get_ai_smell_task", args, cancellationToken: ct)
            .ConfigureAwait(false);
        var status = ExtractTextField(result, "status") ?? "Unknown";
        var payload = ExtractTextField(result, "result");
        return new AiSmellTaskSnapshot(taskId, status, payload);
    }

    private async Task<McpClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "Kata.App bridge",
                Endpoint = _endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
            });
            _client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static string? ExtractTextField(CallToolResult result, string fieldName)
    {
        // MCP tools return structured content as a JSON blob in the text content. Parse it
        // and pluck the requested field. Falls back to null on any shape mismatch.
        foreach (var content in result.Content)
        {
            if (content is not TextContentBlock textBlock) continue;
            try
            {
                using var doc = JsonDocument.Parse(textBlock.Text);
                if (doc.RootElement.TryGetProperty(fieldName, out var prop))
                {
                    return prop.ValueKind switch
                    {
                        JsonValueKind.String => prop.GetString(),
                        JsonValueKind.Null => null,
                        _ => prop.GetRawText(),
                    };
                }
            }
            catch (JsonException)
            {
                // Not JSON — skip.
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } c)
        {
            await c.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        _connectGate.Dispose();
    }
}

public sealed record AiSmellTaskSnapshot(Guid TaskId, string Status, string? Payload)
{
    public bool IsCompleted => string.Equals(Status, "Completed", StringComparison.Ordinal);
    public bool IsFailed => string.Equals(Status, "Failed", StringComparison.Ordinal);
    public bool IsTerminal => IsCompleted || IsFailed;
}
