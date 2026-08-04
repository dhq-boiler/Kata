using System.IO;
using System.Text.Json;

namespace Kata.App.PluginApi;

// ユーザーが入力した Pro ライセンスキーを %LOCALAPPDATA%/Kata/license.json に保存する。
//
// キー検証 (Pro 相当かどうか) は Kata.App.Pro 側の ILicenseValidator が担当。
// Community 版本体はキー文字列の read/write だけを扱い、正当性判定は関与しない。
// PluginApi に置くのは Community / Pro 両方から同じ storage を触るため。
// これにより Pro DLL を後から入れれば、Community で保存済みの同じキーで即アクティベートできる。
public sealed class LicenseStorage
{
    // path 解決失敗 (AV / ディスクフル / 権限) を握りつぶすと read/write が no-op になる。
    // App の static initializer で生成されるので、ここで例外を投げるとアプリが起動不能になる。
    private readonly string? _path;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LicenseStorage()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kata");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "license.json");
        }
        catch
        {
            _path = null;
        }
    }

    public string? LoadKey()
    {
        if (_path is null) return null;
        try
        {
            if (!File.Exists(_path)) return null;
            var doc = JsonSerializer.Deserialize<LicenseFile>(File.ReadAllText(_path), Options);
            return string.IsNullOrWhiteSpace(doc?.LicenseKey) ? null : doc.LicenseKey.Trim();
        }
        catch
        {
            return null;
        }
    }

    public void SaveKey(string? licenseKey)
    {
        if (_path is null) return;
        try
        {
            var doc = new LicenseFile { LicenseKey = string.IsNullOrWhiteSpace(licenseKey) ? null : licenseKey.Trim() };
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, Options));
        }
        catch
        {
            // 書き込み失敗しても本体機能は続行させる (次回起動で戻るだけ)。
        }
    }

    private sealed class LicenseFile
    {
        public string? LicenseKey { get; set; }
    }
}
