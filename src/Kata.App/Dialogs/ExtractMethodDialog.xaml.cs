using System.Windows;

namespace Kata.App.Dialogs;

public partial class ExtractMethodDialog : Window
{
    public ExtractMethodDialog(string containingMemberLabel, string suggestedName = "NewMethod")
    {
        InitializeComponent();
        MemberText.Text = containingMemberLabel;
        NameBox.Text = suggestedName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string NewMethodName { get; private set; } = string.Empty;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        NewMethodName = name;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
