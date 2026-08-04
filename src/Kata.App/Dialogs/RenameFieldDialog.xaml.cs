using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class RenameFieldDialog : Window
{
    private readonly MemberItemViewModel[] _fields;

    public RenameFieldDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;

        _fields = owner.Members.Where(m => m.Kind == MemberKind.Field).ToArray();
        foreach (var f in _fields) FieldCombo.Items.Add(f.Name);
        if (_fields.Length > 0)
        {
            FieldCombo.SelectedIndex = 0;
            NewNameBox.Text = _fields[0].Name;
            NewNameBox.SelectAll();
        }
        else
        {
            OkButton.IsEnabled = false;
        }
        Loaded += (_, _) => NewNameBox.Focus();
    }

    public MemberItemViewModel? SelectedField { get; private set; }
    public string NewName { get; private set; } = string.Empty;

    private void OnFieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FieldCombo.SelectedIndex >= 0 && FieldCombo.SelectedIndex < _fields.Length)
        {
            NewNameBox.Text = _fields[FieldCombo.SelectedIndex].Name;
            NewNameBox.SelectAll();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (FieldCombo.SelectedIndex < 0) return;
        var newName = NewNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;
        var field = _fields[FieldCombo.SelectedIndex];
        if (string.Equals(field.Name, newName, System.StringComparison.Ordinal)) return;
        SelectedField = field;
        NewName = newName;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
