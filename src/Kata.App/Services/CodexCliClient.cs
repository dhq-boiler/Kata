namespace Kata.App.Services;

// `codex exec [--model <id>]` — OpenAI Codex CLI in headless / non-interactive mode.
// Reads prompt from stdin (piped by the base class), writes model response to stdout.
// Billing follows the user's local Codex install (ChatGPT subscription or API key).
public sealed class CodexCliClient : SubprocessCliClient
{
    public CodexCliClient(IAppSettingsStore settings) : base(
        Environment.GetEnvironmentVariable("KATA_CODEX_CLI") ?? "codex",
        modelFlag: "--model",
        modelProvider: () => settings.Load().CodexModel,
        "exec")
    { }
}
