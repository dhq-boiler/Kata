using System.Windows;
using Kata.App.ViewModels;

namespace Kata.App.Dialogs;

public partial class ReplaceMagicNumberDialog : Window
{
    public ReplaceMagicNumberDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        Loaded += (_, _) => LiteralBox.Focus();
    }

    public string LiteralValue { get; private set; } = string.Empty;
    public string ConstantName { get; private set; } = string.Empty;
    public string ConstantType { get; private set; } = "int";

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var lit = LiteralBox.Text.Trim();
        var name = ConstantNameBox.Text.Trim();
        var type = ConstantTypeBox.Text.Trim();
        if (string.IsNullOrEmpty(lit) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
        {
            return;
        }
        LiteralValue = lit;
        ConstantName = name;
        ConstantType = type;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
