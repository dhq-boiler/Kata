using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;

namespace Kata.App.Dialogs;

public partial class TeaseApartInheritanceDialog : Window
{
    public TeaseApartInheritanceDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        SecondaryNameBox.Text = $"{owner.Name}Aspect";
        SubclassBox.Text = "AspectA, AspectB";
        FieldBox.Text = "_aspect";
        Loaded += (_, _) => { SecondaryNameBox.Focus(); SecondaryNameBox.SelectAll(); };
    }

    public string SecondaryHierarchyName { get; private set; } = string.Empty;
    public IReadOnlyList<string> SecondarySubclassNames { get; private set; } = Array.Empty<string>();
    public string DelegationFieldName { get; private set; } = string.Empty;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var secondary = SecondaryNameBox.Text.Trim();
        var field = FieldBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(secondary) || string.IsNullOrWhiteSpace(field)) return;

        var subs = SubclassBox.Text
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (subs.Length == 0) return;

        SecondaryHierarchyName = secondary;
        SecondarySubclassNames = subs;
        DelegationFieldName = field;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
