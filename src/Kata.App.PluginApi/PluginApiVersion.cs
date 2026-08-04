namespace Kata.App.PluginApi;

// PluginApi の contract バージョン。Community と Pro DLL のインターフェース互換範囲を
// 明示する。IProFeatures / ILicenseValidator / LicenseInfo にメンバーを追加した際は
// Minor を bump、既存メンバーの削除・シグネチャ変更は Major を bump する。
//
// ProLoader は Pro DLL 内の PluginApiVersion.Current を読んで、Major が一致しない場合
// (= 破壊的変更) は silent downgrade ではなく明示的にロード失敗として扱う。
// Minor mismatch は許容 (前方互換のはず) だがログには残す。
public static class PluginApiVersion
{
    public const int Major = 1;
    public const int Minor = 1; // +Deactivate() on IProFeatures

    public static string Display => $"{Major}.{Minor}";
}
