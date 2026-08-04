namespace Kata.App.PluginApi;

// Kata Pro のライセンスキー検証。Pro 側 (Kata.App.Pro) で実装、Community 側からは
// IProFeatures 経由で結果 (LicenseInfo) だけ見える。
//
// 実装は当面 stub (キーの形式チェック + local storage)。将来的には Lemon Squeezy
// license API を叩いてサーバー側検証に切り替える。
public interface ILicenseValidator
{
    LicenseInfo Validate(string? licenseKey);
}
