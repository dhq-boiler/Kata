namespace Kata.App.PluginApi;

// Pro 機能ゲート。Community 版本体では NoOpProFeatures が常に IsPro=false を返す。
// Pro 版 (Kata.App.Pro.dll) が起動時に ProLoader 経由でロードされ、実装が差し替わる。
public interface IProFeatures
{
    bool IsPro { get; }

    // Pro 状態の詳細 (ライセンス種別、期限など)。UI 表示や課金導線判定に使う。
    // Community (未アクティベート) では Status=Community、キーは null。
    // Pro DLL のロード失敗時は Status=Community + DisplayMessage にエラー概要が入る。
    LicenseInfo License { get; }
}

public sealed class NoOpProFeatures : IProFeatures
{
    public bool IsPro => false;
    public LicenseInfo License { get; }

    public NoOpProFeatures()
    {
        License = LicenseInfo.Community();
    }

    // Pro DLL のロード失敗など、Community 動作に fallback したときに UI で警告表示させる
    // ためのオーバーロード。DisplayMessage 経由で Preferences の Pro タブに出る。
    public NoOpProFeatures(string diagnosticMessage)
    {
        License = LicenseInfo.Community() with { DisplayMessage = diagnosticMessage };
    }
}
