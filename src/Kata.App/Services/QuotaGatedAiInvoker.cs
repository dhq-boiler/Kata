using Kata.App.PluginApi;

namespace Kata.App.Services;

// Community 版で AI 呼び出しに月次上限を挟むデコレータ。
//
// - Pro 版 (IProFeatures.IsPro=true) では素通し。
// - Community 版では呼び出し前に AiUsageStore を確認、超過なら AiQuotaExceededException を投げる。
// - カウントは成功時のみ進める。timeout / cancel / 例外は消費しない。
//   ユーザーが CLI 未インストールで失敗したケースを 1 回消費されるのは不公平なため。
// - Claude / Codex は同一のカウンタを共有する。「backend を切り替えれば実質倍」の
//   抜け穴を防ぐため単一 budget。
public sealed class QuotaGatedAiInvoker : IAiInvoker
{
    private readonly IAiInvoker _inner;
    private readonly IProFeatures _pro;
    private readonly AiUsageStore _usage;

    public QuotaGatedAiInvoker(IAiInvoker inner, IProFeatures pro, AiUsageStore usage)
    {
        _inner = inner;
        _pro = pro;
        _usage = usage;
    }

    public async Task<string> AskAsync(string prompt, CancellationToken ct)
    {
        if (_pro.IsPro)
        {
            return await _inner.AskAsync(prompt, ct).ConfigureAwait(false);
        }

        var snapshot = _usage.Snapshot();
        if (snapshot.IsExhausted)
        {
            throw new AiQuotaExceededException(snapshot);
        }

        var response = await _inner.AskAsync(prompt, ct).ConfigureAwait(false);
        _usage.RecordSuccess();
        return response;
    }

    // 同一 transaction の followup 用: quota check も increment も skip。
    // caller は「最初の AskAsync が成功した後」だけ呼ぶこと。
    public Task<string> AskUnmeteredAsync(string prompt, CancellationToken ct)
        => _inner.AskAsync(prompt, ct);
}
