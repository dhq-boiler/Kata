using System.Windows;
using System.Windows.Controls;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class AddGhostTypeDialog : Window
{
    public AddGhostTypeDialog(string? initialNamespace)
    {
        InitializeComponent();

        NamespaceBox.Text = initialNamespace ?? string.Empty;
        Loaded += (_, _) =>
        {
            TypeNameBox.Focus();
        };
    }

    public string TypeName { get; private set; } = string.Empty;
    public string NamespaceName { get; private set; } = string.Empty;
    public TypeKind Kind { get; private set; } = TypeKind.Class;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var name = TypeNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var ns = NamespaceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ns))
        {
            return;
        }

        var kindText = (KindCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Class";
        if (!Enum.TryParse<TypeKind>(kindText, ignoreCase: true, out var kind))
        {
            kind = TypeKind.Class;
        }

        TypeName = name;
        NamespaceName = ns;
        Kind = kind;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
