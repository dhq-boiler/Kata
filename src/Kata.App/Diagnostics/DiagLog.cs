using System.IO;

namespace Kata.App.Diagnostics;

/// <summary>
/// 環境設定「診断モード」が on のとき、apply / diff overlay の逐次ログを
/// <see cref="FilePath"/> (%TEMP%\kata-diag.log) に追記する。
///
/// hot-path (ApplyAndReloadAsync など) から呼ばれるので、無効時は volatile bool
/// 1 個の読み取りだけで即抜ける。設定変更は
/// <see cref="ViewModels.PreferencesViewModel"/> の Ok() から <see cref="Enabled"/>
/// に反映される。
/// </summary>
internal static class DiagLog
{
    private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kata-diag.log");
    private static readonly object Gate = new();

    private static volatile bool _enabled;

    public static string FilePath => LogPath;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Line(string message)
    {
        if (!_enabled) return;
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"{stamp} {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // 診断用なので握りつぶす — ファイル書けなくても本処理に影響させない
        }
    }
}
