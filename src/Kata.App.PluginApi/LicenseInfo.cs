namespace Kata.App.PluginApi;

public enum LicenseStatus
{
    Community,   // 未アクティベート = Community 版として動作
    Active,      // 有効なキーで Pro 機能 unlock
    Invalid,     // キーが不正 (形式 or サーバー拒否)
    Expired,     // 期限切れ (Team/Business の subscription 切れ)
}

public enum LicenseTier
{
    None,        // Community
    Individual,  // $49 buyout
    Team,        // $150/seat/year
    Business,    // $290/seat/year (SSO / SLA / 請求書)
}

// Pro 機能ゲート判定 + UI 表示のための license 状態スナップショット。
// UTC タイムスタンプで持つ (表示側で local time に変換)。
public sealed record LicenseInfo(
    LicenseStatus Status,
    LicenseTier Tier,
    string? Email,
    DateTime? IssuedAtUtc,
    DateTime? ExpiresAtUtc,
    string? DisplayMessage)
{
    public bool IsPro => Status == LicenseStatus.Active;

    public static LicenseInfo Community() => new(
        LicenseStatus.Community,
        LicenseTier.None,
        Email: null,
        IssuedAtUtc: null,
        ExpiresAtUtc: null,
        DisplayMessage: null);
}
