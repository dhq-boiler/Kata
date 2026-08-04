using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class RemoveParameterDialog : Window
{
    private readonly MemberItemViewModel[] _methods;

    public RemoveParameterDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _methods = owner.Members.Where(m => m.Kind == MemberKind.Method).ToArray();
        foreach (var m in _methods) MethodCombo.Items.Add(m.DisplayLine);
        if (_methods.Length > 0) MethodCombo.SelectedIndex = 0;
        OkButton.IsEnabled = _methods.Length > 0;
        Loaded += (_, _) => NameBox.Focus();
    }

    public MemberItemViewModel? SelectedMethod { get; private set; }
    public string ParameterName { get; private set; } = string.Empty;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0) return;
        var n = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(n)) return;
        SelectedMethod = _methods[MethodCombo.SelectedIndex];
        ParameterName = n;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
