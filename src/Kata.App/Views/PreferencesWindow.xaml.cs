using System.Windows;
using Kata.App.Localization;
using Kata.App.ViewModels;

namespace Kata.App.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow(PreferencesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.Committed += (_, _) => { DialogResult = true; Close(); };
        viewModel.Cancelled += (_, _) => { DialogResult = false; Close(); };

        // Deactivate はネットワーク経由で LS 側の activation を消す取り返しの効かない操作。
        // 誤爆防止に必ず確認ダイアログを挟む。View 層で MessageBox を出したいので
        // ViewModel からのイベントを hook する。
        viewModel.DeactivateAsking = () => MessageBox.Show(
            this,
            Strings.Preferences_Pro_Deactivate_ConfirmBody,
            Strings.Preferences_Pro_Deactivate_ConfirmTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
