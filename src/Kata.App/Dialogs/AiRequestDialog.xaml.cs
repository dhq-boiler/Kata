using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kata.App.Localization;

namespace Kata.App.Dialogs;

/// <summary>
/// AI (Claude / Codex) の CLI 応答待ちを可視化する modeless 待機ダイアログ。
///
/// 呼び出し側 (MainWindow.xaml.cs / AskAiSmellFixAsync) が Show() して、
/// await ask(...) の完了 / 例外 / タイムアウトで <see cref="CloseIfOpen"/> する。
/// 内部 Timer で経過秒を更新して「体感生きてる感」を出す。
/// Cancel ボタン / タイトルバー✕ / ESC のいずれかで <see cref="CancellationTokenSource.Cancel"/>。
/// </summary>
public partial class AiRequestDialog : Window
{
    private readonly CancellationTokenSource _cts;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly DateTime _startedUtc;
    private bool _closed;

    public AiRequestDialog(string headline, string subtitle, CancellationTokenSource cts)
    {
        InitializeComponent();
        _cts = cts;
        _startedUtc = DateTime.UtcNow;

        HeadlineText.Text = headline;
        SubtitleText.Text = subtitle;
        ElapsedText.Text = Strings.AiRequest_Elapsed_Initial;

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
    }

    public void CloseIfOpen()
    {
        if (_closed) return;
        _closed = true;
        _elapsedTimer.Stop();
        Close();
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _startedUtc;
        ElapsedText.Text = elapsed.TotalMinutes >= 1
            ? string.Format(Strings.AiRequest_Elapsed_MinutesSeconds_Format, (int)elapsed.TotalMinutes, elapsed.Seconds)
            : string.Format(Strings.AiRequest_Elapsed_Seconds_Format, elapsed.Seconds);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try { _cts.Cancel(); } catch { }
        CloseIfOpen();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // タイトルバーの ✕ で閉じても cancel と等価にする。await 中の ask がキャンセルされる。
        if (!_closed)
        {
            try { _cts.Cancel(); } catch { }
        }
        _elapsedTimer.Stop();
        _closed = true;
        base.OnClosing(e);
    }
}
