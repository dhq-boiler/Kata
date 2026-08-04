using System.ComponentModel;
using Kata.App.Localization;
using Kata.App.PluginApi;

namespace Kata.App.Services;

// smell popup 内の "AI に提案させる" ボタンに「残 X/10」バッジを出すための
// UI 側 observer。static singleton にしてあるのは、Popup / DataTemplate 内から
// ancestor 経由の binding が届きにくいため。 Community 版だけ有効、Pro 版では
// バッジ非表示 (Show=false)。
//
// caller 側で Refresh() を呼ぶタイミングで snapshot を読み直す:
//   - popup 開いた瞬間 (OnAskAiSmellFixMenuOpen)
//   - AI 呼び出し完了直後 (成功 / quota 超過どちらも)
public sealed class AiQuotaObserver : INotifyPropertyChanged
{
    public static AiQuotaObserver Instance { get; } = new();

    private readonly IProFeatures _pro;
    private readonly AiUsageStore _usage;

    private string _badgeText = string.Empty;
    private bool _show;

    private AiQuotaObserver()
    {
        _pro = App.ProFeatures;
        _usage = App.AiUsage;
        Refresh();
    }

    public string BadgeText
    {
        get => _badgeText;
        private set
        {
            if (_badgeText == value) return;
            _badgeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeText)));
        }
    }

    public bool Show
    {
        get => _show;
        private set
        {
            if (_show == value) return;
            _show = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Show)));
        }
    }

    public void Refresh()
    {
        if (_pro.IsPro)
        {
            Show = false;
            BadgeText = string.Empty;
            return;
        }

        var snap = _usage.Snapshot();
        BadgeText = string.Format(Strings.AiQuota_BadgeRemaining_Format, snap.Remaining, snap.Limit);
        Show = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
