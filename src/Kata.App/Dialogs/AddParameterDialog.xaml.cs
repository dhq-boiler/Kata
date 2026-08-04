using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class AddParameterDialog : Window
{
    private readonly MemberItemViewModel[] _methods;

    public AddParameterDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _methods = owner.Members.Where(m => m.Kind == MemberKind.Method).ToArray();
        foreach (var m in _methods) MethodCombo.Items.Add(m.DisplayLine);
        if (_methods.Length > 0) MethodCombo.SelectedIndex = 0;
        OkButton.IsEnabled = _methods.Length > 0;
        Loaded += (_, _) => TypeBox.Focus();
    }

    public MemberItemViewModel? SelectedMethod { get; private set; }
    public string ParameterType { get; private set; } = string.Empty;
    public string ParameterName { get; private set; } = string.Empty;
    public string? DefaultValue { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0) return;
        var t = TypeBox.Text.Trim();
        var n = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(n)) return;
        SelectedMethod = _methods[MethodCombo.SelectedIndex];
        ParameterType = t;
        ParameterName = n;
        var d = DefaultBox.Text.Trim();
        DefaultValue = string.IsNullOrEmpty(d) ? null : d;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
