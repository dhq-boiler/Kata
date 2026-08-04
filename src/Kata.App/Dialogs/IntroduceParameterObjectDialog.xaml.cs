using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class IntroduceParameterObjectDialog : Window
{
    private readonly MemberItemViewModel[] _methods;

    public IntroduceParameterObjectDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _methods = owner.Members.Where(m => m.Kind == MemberKind.Method).ToArray();
        foreach (var m in _methods) MethodCombo.Items.Add(m.DisplayLine);
        if (_methods.Length > 0)
        {
            MethodCombo.SelectedIndex = 0;
            ObjectNameBox.Text = owner.Name + "Args";
        }
        OkButton.IsEnabled = _methods.Length > 0;
        Loaded += (_, _) => ObjectNameBox.Focus();
    }

    public MemberItemViewModel? SelectedMethod { get; private set; }
    public string ObjectName { get; private set; } = string.Empty;
    public string ParameterName { get; private set; } = "args";

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0) return;
        var name = ObjectNameBox.Text.Trim();
        var pname = ParameterNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pname)) return;
        SelectedMethod = _methods[MethodCombo.SelectedIndex];
        ObjectName = name;
        ParameterName = pname;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
