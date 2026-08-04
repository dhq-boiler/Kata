using System.Diagnostics;
using System.Windows;
using Kata.App.Localization;
using Kata.App.Services;

namespace Kata.App.Dialogs;

// Community 版で AI 相談が月次上限に達したときに出すダイアログ。
// 素の MessageBox でなく専用ダイアログにすることで、購入ボタンで直接ブラウザを
// 起動できるようにしている (Fable M4: 「守るのはファネル」の終端強化)。
public partial class AiQuotaExceededDialog : Window
{
    private const string PurchaseUrl = "https://kata.dhq-boiler.dev/pro";

    public bool EnterKeyRequested { get; private set; }

    public AiQuotaExceededDialog(AiUsageSnapshot snapshot)
    {
        InitializeComponent();
        HeadingText.Text = Strings.AiQuota_Dialog_Heading;
        BodyText.Text = string.Format(Strings.AiQuota_Dialog_Body_Format, snapshot.Limit, snapshot.NextResetUtc);
    }

    private void OnPurchaseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute=true で既定ブラウザに URL を渡す。exe 単独起動より安全
            // (URL のシェル実行は Windows のブラウザ関連付けに従う)。
            Process.Start(new ProcessStartInfo
            {
                FileName = PurchaseUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 起動失敗時は無視。ユーザーは Close で戻れる。
        }
    }

    private void OnEnterKeyClick(object sender, RoutedEventArgs e)
    {
        // 呼び出し側 (MainWindow) がこれを見て Preferences → Pro タブを開く。
        EnterKeyRequested = true;
        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
