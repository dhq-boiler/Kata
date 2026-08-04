using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kata.App.Services;

public static class WindowChromeHelper
{
    /// <summary>
    /// 最大化ボタンの上にカーソルがあることを、テンプレートのトリガへ伝えるための印。
    ///
    /// スナップレイアウトを出すには、その領域のヒットテストを OS に返してしまう必要がある。
    /// すると WPF にはマウスが入ってこなくなり <see cref="UIElement.IsMouseOver"/> が
    /// 立たない。ホバーの見た目が消えるので、代わりにこれを立てる。
    /// </summary>
    public static readonly DependencyProperty IsSnapHoverProperty =
        DependencyProperty.RegisterAttached(
            "IsSnapHover",
            typeof(bool),
            typeof(WindowChromeHelper),
            new PropertyMetadata(false));

    public static bool GetIsSnapHover(DependencyObject obj)
        => (bool)obj.GetValue(IsSnapHoverProperty);

    public static void SetIsSnapHover(DependencyObject obj, bool value)
        => obj.SetValue(IsSnapHoverProperty, value);

    public static readonly DependencyProperty LogoSourceProperty =
        DependencyProperty.RegisterAttached(
            "LogoSource",
            typeof(ImageSource),
            typeof(WindowChromeHelper),
            new PropertyMetadata(null));

    public static ImageSource? GetLogoSource(DependencyObject obj)
        => (ImageSource?)obj.GetValue(LogoSourceProperty);

    public static void SetLogoSource(DependencyObject obj, ImageSource? value)
        => obj.SetValue(LogoSourceProperty, value);

    public static readonly DependencyProperty TitleTextVisibilityProperty =
        DependencyProperty.RegisterAttached(
            "TitleTextVisibility",
            typeof(Visibility),
            typeof(WindowChromeHelper),
            new PropertyMetadata(Visibility.Visible));

    public static Visibility GetTitleTextVisibility(DependencyObject obj)
        => (Visibility)obj.GetValue(TitleTextVisibilityProperty);

    public static void SetTitleTextVisibility(DependencyObject obj, Visibility value)
        => obj.SetValue(TitleTextVisibilityProperty, value);

    /// <summary>
    /// タイトルバーのキャプションボタン (Min/Max/Close) 左側に載せる任意コンテンツ。
    /// 例えば「開いているソリューション名」など、ロゴ / タイトル / メニューとは別に
    /// 右端に寄せて出したい情報を置くための領域。
    /// </summary>
    public static readonly DependencyProperty RightHeaderContentProperty =
        DependencyProperty.RegisterAttached(
            "RightHeaderContent",
            typeof(object),
            typeof(WindowChromeHelper),
            new PropertyMetadata(null));

    public static object? GetRightHeaderContent(DependencyObject obj)
        => obj.GetValue(RightHeaderContentProperty);

    public static void SetRightHeaderContent(DependencyObject obj, object? value)
        => obj.SetValue(RightHeaderContentProperty, value);

    /// <summary>
    /// XAML から <c>svc:WindowChromeHelper.IsAttached="True"</c> と書いて
    /// <see cref="Attach"/> を効かせるための添付プロパティ。
    /// コードビハインドのコンストラクタで Attach を呼ばずに済ませるために用意している。
    /// </summary>
    public static readonly DependencyProperty IsAttachedProperty =
        DependencyProperty.RegisterAttached(
            "IsAttached",
            typeof(bool),
            typeof(WindowChromeHelper),
            new PropertyMetadata(false, OnIsAttachedChanged));

    public static bool GetIsAttached(DependencyObject obj)
        => (bool)obj.GetValue(IsAttachedProperty);

    public static void SetIsAttached(DependencyObject obj, bool value)
        => obj.SetValue(IsAttachedProperty, value);

    private static void OnIsAttachedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Window window && e.NewValue is true) Attach(window);
    }

    public static void Attach(Window window)
    {
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(window),
            (_, e) => e.CanExecute = window.ResizeMode != ResizeMode.NoResize));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
            (_, _) => SystemCommands.MaximizeWindow(window),
            (_, e) => e.CanExecute = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
            (_, _) => SystemCommands.RestoreWindow(window),
            (_, e) => e.CanExecute = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => SystemCommands.CloseWindow(window)));

        window.StateChanged += (_, _) => UpdateButtons(window);
        window.Loaded += (_, _) => UpdateButtons(window);
        // フックは Loaded で足す。WindowChrome も同じウィンドウで WM_NCHITTEST を
        // 見ていて、後から入れたほうが最終的な応答を決める。SourceInitialized の
        // 時点だと、こちらの答えが WindowChrome の HTCLIENT に上書きされる。
        window.Loaded += (_, _) => HookSnapLayout(window);
    }

    private static void UpdateButtons(Window window)
    {
        if (window.Template?.FindName("MaxButton", window) is not Button max) return;
        if (window.Template?.FindName("RestoreButton", window) is not Button restore) return;
        var maximized = window.WindowState == WindowState.Maximized;
        max.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        restore.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    // ここから下は、Windows 11 のスナップレイアウトを自前のタイトルバーで出すための仕掛け。
    //
    // OS はスナップレイアウトの吹き出しを「最大化ボタンの上にカーソルがある」と判断したときに
    // 出す。その判断は WM_NCHITTEST への応答だけで行われるので、標準のタイトルバーを外して
    // 自前のボタンを置いた時点で、名乗り出ない限り一生出てこない。

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCMOUSELEAVE = 0x02A2;

    /// <summary>「ここは最大化ボタンです」と OS に答えるための値。</summary>
    private const int HTMAXBUTTON = 9;

    /// <summary>Loaded は再表示のたびに来ることがあるので、二重に足さない。</summary>
    private static readonly HashSet<Window> Hooked = [];

    private static void HookSnapLayout(Window window)
    {
        if (!Hooked.Add(window)) return;
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            Hooked.Remove(window);
            return;
        }

        window.Closed += (_, _) => Hooked.Remove(window);
        source.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
            => WndProc(window, msg, lParam, ref handled));
    }

    private static nint WndProc(Window window, int msg, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (!IsOverMaximizeButton(window, lParam, out var button))
                {
                    // 名乗り出ない。ここで handled を立てると、枠のつかみもドラッグも死ぬ。
                    ClearHover(window);
                    return nint.Zero;
                }

                SetHover(window, button);
                handled = true;
                return HTMAXBUTTON;

            case WM_NCMOUSELEAVE:
                ClearHover(window);
                return nint.Zero;

            case WM_NCLBUTTONDOWN:
                if (!IsOverMaximizeButton(window, lParam, out var pressed)) return nint.Zero;

                // 押した瞬間に動かす。本来のボタンは離したときに動くが、この領域は
                // WPF の外なので、押下を飲み込んだ後に離上が必ず届く保証が無い。
                // 押しっぱなしで外へ逃げても最大化されるのが唯一の違いで、
                // 押しても何も起きないより軽い。
                handled = true;
                if (pressed.Command?.CanExecute(null) == true) pressed.Command.Execute(null);
                return nint.Zero;

            default:
                return nint.Zero;
        }
    }

    /// <summary>
    /// いま出ている最大化 / 元に戻すボタンの上に、その座標があるか。
    ///
    /// 座標は画面の物理ピクセルで飛んでくる。ボタンの寸法は WPF の論理単位なので、
    /// 角を二点とも <see cref="Visual.PointToScreen"/> に通して物理側で比べる。
    /// 拡大率を自分で掛けると、150% 表示の環境でだけ当たらなくなる。
    /// </summary>
    private static bool IsOverMaximizeButton(Window window, nint lParam, out Button button)
    {
        button = null!;

        // 最大化できない窓で名乗り出ると、押しても何も起きないボタンが生まれる。
        if (window.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)) return false;

        var name = window.WindowState == WindowState.Maximized ? "RestoreButton" : "MaxButton";
        if (window.Template?.FindName(name, window) is not Button found) return false;
        if (found.Visibility != Visibility.Visible || found.ActualWidth <= 0) return false;

        // 下位・上位ワードとも符号付き。副ディスプレイが左や上にあると負になる。
        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);

        try
        {
            var topLeft = found.PointToScreen(new Point(0, 0));
            var bottomRight = found.PointToScreen(new Point(found.ActualWidth, found.ActualHeight));
            if (x < topLeft.X || x >= bottomRight.X || y < topLeft.Y || y >= bottomRight.Y) return false;
        }
        catch (InvalidOperationException)
        {
            // まだ画面に載っていない。次のヒットテストで拾えばよい。
            return false;
        }

        button = found;
        return true;
    }

    private static void SetHover(Window window, Button button)
    {
        if (!GetIsSnapHover(button)) SetIsSnapHover(button, true);
    }

    private static void ClearHover(Window window)
    {
        foreach (var name in new[] { "MaxButton", "RestoreButton" })
        {
            if (window.Template?.FindName(name, window) is Button button && GetIsSnapHover(button))
            {
                SetIsSnapHover(button, false);
            }
        }
    }
}
