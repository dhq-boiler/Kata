using System.Windows;
using System.Windows.Input;

namespace Kata.App.Dialogs;

public partial class RenameDialog : Window
{
    public string NewName { get; private set; } = string.Empty;

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        CurrentNameText.Text = currentName;
        NewNameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NewNameBox.Focus();
            NewNameBox.SelectAll();
        };
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var value = NewNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        NewName = value;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnNewNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
