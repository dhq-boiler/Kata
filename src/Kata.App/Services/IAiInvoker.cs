namespace Kata.App.Services;

// AI CLI クライアント (Claude / Codex etc.) の抽象。QuotaGatedAiInvoker で
// デコレートして Community 版の月次上限を挟むために切り出した interface。
public interface IAiInvoker
{
    Task<string> AskAsync(string prompt, CancellationToken ct);

    // 同一 transaction の followup (LLM の incomplete diff を受けての再問い合わせなど
    // 論理的に最初の 1 リクエストの延長にあたるもの) 用。quota check / increment を
    // スキップする。callers は「最初は AskAsync、続きは AskUnmeteredAsync」を守ること。
    Task<string> AskUnmeteredAsync(string prompt, CancellationToken ct);
}
