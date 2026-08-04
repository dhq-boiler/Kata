using System.Windows;

namespace Kata.App.Dialogs;

public partial class IntroduceAssertionDialog : Window
{
    public IntroduceAssertionDialog(string containingMemberLabel)
    {
        InitializeComponent();
        MemberText.Text = containingMemberLabel;
        CondBox.Text = "arg != null";
        Loaded += (_, _) => { CondBox.Focus(); CondBox.SelectAll(); };
    }

    public string AssertionExpression { get; private set; } = string.Empty;
    public string? Message { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var expr = CondBox.Text.Trim();
        if (string.IsNullOrEmpty(expr)) return;
        AssertionExpression = expr;
        var msg = MsgBox.Text.Trim();
        Message = string.IsNullOrEmpty(msg) ? null : msg;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
