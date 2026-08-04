using System.Windows;

namespace Kata.App.Dialogs;

public partial class DecomposeConditionalDialog : Window
{
    public DecomposeConditionalDialog(string containingMemberLabel)
    {
        InitializeComponent();
        MemberText.Text = containingMemberLabel;
        CondBox.Text = "IsCondition";
        ThenBox.Text = "HandleThen";
        ElseBox.Text = "HandleElse";
        Loaded += (_, _) => { CondBox.Focus(); CondBox.SelectAll(); };
    }

    public string ConditionMethodName { get; private set; } = string.Empty;
    public string ThenMethodName { get; private set; } = string.Empty;
    public string? ElseMethodName { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var cond = CondBox.Text.Trim();
        var then = ThenBox.Text.Trim();
        var els = ElseBox.Text.Trim();
        if (string.IsNullOrEmpty(cond) || string.IsNullOrEmpty(then)) return;
        ConditionMethodName = cond;
        ThenMethodName = then;
        ElseMethodName = string.IsNullOrEmpty(els) ? null : els;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
