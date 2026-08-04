using Kata.Mcp;
using Microsoft.Extensions.Logging;

// Streamable HTTP transport per MCP 2026-07-28 spec.
// Stateless by default — no Mcp-Session-Id, cross-call state travels as tool arguments.
// Legacy stdio transport was retired here; client configuration must switch to HTTP.
// Bind endpoint: default http://localhost:7345/mcp, override via `--urls` or KATA_MCP_URLS env.

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<KataSession>();
builder.Services.AddSingleton<AiTaskQueue>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var defaultUrl = Environment.GetEnvironmentVariable("KATA_MCP_URLS") ?? "http://localhost:7345";
builder.WebHost.UseUrls(defaultUrl);

var app = builder.Build();

app.MapMcp("/mcp");

await app.RunAsync().ConfigureAwait(false);
