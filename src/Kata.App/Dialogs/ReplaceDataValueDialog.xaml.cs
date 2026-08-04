using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ReplaceDataValueDialog : Window
{
    private readonly MemberItemViewModel[] _fields;

    public ReplaceDataValueDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _fields = owner.Members
            .Where(m => m.Kind is MemberKind.Field or MemberKind.Property)
            .ToArray();
        foreach (var f in _fields) FieldCombo.Items.Add(f.DisplayLine);
        if (_fields.Length > 0)
        {
            FieldCombo.SelectedIndex = 0;
            WrapperNameBox.Text = _fields[0].Name;
        }
        OkButton.IsEnabled = _fields.Length > 0;
        Loaded += (_, _) => WrapperNameBox.Focus();
    }

    public MemberItemViewModel? SelectedField { get; private set; }
    public string WrapperClassName { get; private set; } = string.Empty;
    public string InnerFieldName { get; private set; } = "Value";

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (FieldCombo.SelectedIndex < 0) return;
        var name = WrapperNameBox.Text.Trim();
        var inner = InnerNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(inner)) return;
        SelectedField = _fields[FieldCombo.SelectedIndex];
        WrapperClassName = name;
        InnerFieldName = inner;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
