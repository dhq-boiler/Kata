using System.Diagnostics;
using System.Text;

namespace Kata.App.Services;

// Base class: pipes the prompt into a subprocess's stdin, reads stdout, returns it.
// Cancellation kills the process. Meant for headless AI CLIs — ClaudeCliClient and
// CodexCliClient supply the executable + initial args + how to inject a model id.
public abstract class SubprocessCliClient : IAiInvoker
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _initialArgs;
    private readonly string? _modelFlag;
    private readonly Func<string?>? _modelProvider;

    protected SubprocessCliClient(string executable, params string[] initialArgs)
        : this(executable, modelFlag: null, modelProvider: null, initialArgs) { }

    protected SubprocessCliClient(
        string executable,
        string? modelFlag,
        Func<string?>? modelProvider,
        params string[] initialArgs)
    {
        _executable = executable;
        _initialArgs = initialArgs;
        _modelFlag = modelFlag;
        _modelProvider = modelProvider;
    }

    public async Task<string> AskAsync(string prompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in _initialArgs) psi.ArgumentList.Add(arg);

        // モデル ID は AskAsync のたびに Provider を叩き直す。環境設定でユーザーが
        // 変えた直後の次回問い合わせから反映させたいので、コンストラクタ時点で
        // 焼き付けない。空文字/未設定なら CLI 側の既定モデル。
        if (_modelFlag is not null && _modelProvider is not null)
        {
            var model = _modelProvider();
            if (!string.IsNullOrWhiteSpace(model))
            {
                psi.ArgumentList.Add(_modelFlag);
                psi.ArgumentList.Add(model.Trim());
            }
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{_executable}'");

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
        await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 400 ? stderr[^400..] : stderr;
            throw new InvalidOperationException(
                $"'{_executable} {string.Join(' ', _initialArgs)}' exited with code {proc.ExitCode}. stderr: {tail}");
        }
        return stdout;
    }

    // ここでは quota 概念が無いので metered / unmetered どちらも同じ経路。
    // Community 版で QuotaGatedAiInvoker が wrap したときに差が出る。
    public Task<string> AskUnmeteredAsync(string prompt, CancellationToken ct) => AskAsync(prompt, ct);
}

// `claude -p [--model <id>]` — Claude Code in print mode. Reads prompt from stdin
// (piped by base class), writes model response to stdout. Billing follows the user's
// Claude Code install (subscription or API key).
public sealed class ClaudeCliClient : SubprocessCliClient
{
    public ClaudeCliClient(IAppSettingsStore settings) : base(
        Environment.GetEnvironmentVariable("KATA_CLAUDE_CLI") ?? "claude",
        modelFlag: "--model",
        modelProvider: () => settings.Load().ClaudeModel,
        "-p")
    { }
}
