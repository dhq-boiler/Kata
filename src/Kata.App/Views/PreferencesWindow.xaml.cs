using System.Windows;
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
    }
}
