namespace Kata.App.Services;

/// <summary>
/// 環境設定の「AI モデル識別子」入力欄で補完候補として並べるリスト。
///
/// 新しいモデルが発表されたら、対応する CLI で `--model <id>` に受け入れられる ID を
/// このリストの先頭に追加する。ここへの追加が本製品のアップデート動機の 1 つになる想定。
///
/// ユーザーは補完候補以外の ID も直接入力できる (ComboBox IsEditable="True")。
/// 空文字を保存すると各 CLI 側の既定モデルが使われる。
/// </summary>
public static class KnownAiModels
{
    // Claude Code CLI (`claude -p --model <id>`) で通る識別子。
    // 新しい順に並べる。
    public static IReadOnlyList<string> Claude { get; } = new[]
    {
        "claude-fable-5",
        "claude-opus-5",
        "claude-sonnet-5",
        "claude-haiku-4-5-20251001",
        "claude-opus-4-7",
        // CLI が受け付けるエイリアス
        "opus",
        "sonnet",
        "haiku",
    };

    // OpenAI Codex CLI (`codex exec --model <id>`) で通る識別子。
    // 新しい順に並べる。
    public static IReadOnlyList<string> Codex { get; } = new[]
    {
        "gpt-5",
        "gpt-5-codex",
        "gpt-5-mini",
        "o3",
        "o3-mini",
        "gpt-4.1",
    };
}
