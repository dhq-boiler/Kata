using System.Windows;

namespace REghZyFramework.Themes;

// 既存プロジェクト由来のカスタムタイトルバー用イベントハンドラ。
// Kata のダイアログは既定の Windows chrome を使うため実質未使用だが、
// 既存プロジェクトの DarkTheme.xaml をそのまま流用する都合上、コードビハインド
// クラスを Kata 側にも用意しておく。
public partial class DarkTheme
{
    private void CloseWindow_Event(object sender, RoutedEventArgs e)
    {
        if (e.Source is not null)
        {
            try
            {
                CloseWind(Window.GetWindow((FrameworkElement)e.Source));
            }
            catch
            {
            }
        }
    }

    private void AutoMinimize_Event(object sender, RoutedEventArgs e)
    {
        if (e.Source is not null)
        {
            try
            {
                MaximizeRestore(Window.GetWindow((FrameworkElement)e.Source));
            }
            catch
            {
            }
        }
    }

    private void Minimize_Event(object sender, RoutedEventArgs e)
    {
        if (e.Source is not null)
        {
            try
            {
                MinimizeWind(Window.GetWindow((FrameworkElement)e.Source));
            }
            catch
            {
            }
        }
    }

    public void CloseWind(Window window)
    {
        window.Close();
    }

    public void MaximizeRestore(Window window)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            window.WindowState = WindowState.Normal;
        }
        else if (window.WindowState == WindowState.Normal)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public void MinimizeWind(Window window)
    {
        window.WindowState = WindowState.Minimized;
    }
}
