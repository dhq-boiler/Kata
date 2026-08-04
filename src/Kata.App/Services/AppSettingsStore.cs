using System.IO;
using System.Text.Json;

namespace Kata.App.Services;

/// <summary>アプリ全体の設定 (ソリューションに紐付かないもの)。</summary>
public sealed class AppSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.System;

    // AI 相談機能で使うモデル識別子。null / 空文字 = 各 CLI の既定モデルを使う。
    // 補完候補は KnownAiModels に置いてあるがユーザーは任意 ID を入力できる。
    public string? ClaudeModel { get; set; }
    public string? CodexModel { get; set; }

    // 診断モード。true で %TEMP%\kata-diag.log に apply / diff overlay の逐次ログを吐く。
    // 通常は off。バグ調査のときに一時的に on にする用途。
    public bool DiagnosticsEnabled { get; set; }
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>
/// %LOCALAPPDATA%/Kata/settings.json に置く。
///
/// 設定が壊れていても起動だけはさせたいので、読み書きの失敗は握りつぶして既定値で続ける。
/// </summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JsonAppSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kata");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 保存できなくても今の画面は切り替わっている。次回起動で戻るだけ
        }
    }
}
