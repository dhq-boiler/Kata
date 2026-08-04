using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;

namespace Kata.App.Dialogs;

public partial class ReplaceTypeCodeWithSubclassesDialog : Window
{
    public ReplaceTypeCodeWithSubclassesDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        SubclassBox.Text = "Engineer, Manager, Salesman";
        Loaded += (_, _) => { SubclassBox.Focus(); SubclassBox.SelectAll(); };
    }

    public IReadOnlyList<string> SubclassNames { get; private set; } = Array.Empty<string>();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var names = SubclassBox.Text
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (names.Length == 0) return;
        SubclassNames = names;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
