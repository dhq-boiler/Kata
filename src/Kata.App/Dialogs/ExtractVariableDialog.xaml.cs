using System.Windows;

namespace Kata.App.Dialogs;

public partial class ExtractVariableDialog : Window
{
    public ExtractVariableDialog(string containingMemberLabel, string suggestedName = "value")
    {
        InitializeComponent();
        MemberText.Text = containingMemberLabel;
        NameBox.Text = suggestedName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string NewVariableName { get; private set; } = string.Empty;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        NewVariableName = name;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
