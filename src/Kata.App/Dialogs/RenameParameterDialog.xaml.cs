using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class RenameParameterDialog : Window
{
    private readonly MemberItemViewModel[] _methods;

    public RenameParameterDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _methods = owner.Members.Where(m => m.Kind == MemberKind.Method).ToArray();
        foreach (var m in _methods) MethodCombo.Items.Add(m.DisplayLine);
        if (_methods.Length > 0)
        {
            MethodCombo.SelectedIndex = 0;
            PopulateOldNames(_methods[0]);
        }
        OkButton.IsEnabled = _methods.Length > 0;
        Loaded += (_, _) => OldNameCombo.Focus();
    }

    public MemberItemViewModel? SelectedMethod { get; private set; }
    public string OldName { get; private set; } = string.Empty;
    public string NewName { get; private set; } = string.Empty;

    private void OnMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0 || MethodCombo.SelectedIndex >= _methods.Length) return;
        PopulateOldNames(_methods[MethodCombo.SelectedIndex]);
    }

    private void PopulateOldNames(MemberItemViewModel method)
    {
        OldNameCombo.Items.Clear();
        foreach (var p in ExtractParameterNames(method.Ref.Signature))
        {
            OldNameCombo.Items.Add(p);
        }
        if (OldNameCombo.Items.Count > 0)
        {
            OldNameCombo.SelectedIndex = 0;
        }
        else
        {
            NewNameBox.Text = string.Empty;
        }
    }

    private void OnOldNameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OldNameCombo.SelectedItem is string s)
        {
            NewNameBox.Text = s;
            NewNameBox.SelectAll();
        }
    }

    private static IEnumerable<string> ExtractParameterNames(string signature)
    {
        int open = signature.IndexOf('(');
        int close = signature.LastIndexOf(')');
        if (open < 0 || close <= open) yield break;
        var inner = signature.Substring(open + 1, close - open - 1);
        if (string.IsNullOrWhiteSpace(inner)) yield break;

        int depth = 0;
        var start = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            char c = i < inner.Length ? inner[i] : ',';
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                var arg = inner.Substring(start, i - start).Trim();
                var name = LastWord(arg);
                if (!string.IsNullOrEmpty(name)) yield return name;
                start = i + 1;
            }
        }
    }

    private static string LastWord(string arg)
    {
        // For "ISource source" → "source". For "int" (no name) → "".
        arg = arg.Trim();
        if (arg.Length == 0) return string.Empty;
        int sp = arg.LastIndexOfAny(new[] { ' ', '\t', '&', '*', '^' });
        return sp >= 0 ? arg.Substring(sp + 1) : string.Empty;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0) return;
        if (OldNameCombo.SelectedItem is not string oldN || string.IsNullOrWhiteSpace(oldN)) return;
        var newN = NewNameBox.Text.Trim();
        if (string.IsNullOrEmpty(newN) || string.Equals(oldN, newN, System.StringComparison.Ordinal)) return;
        SelectedMethod = _methods[MethodCombo.SelectedIndex];
        OldName = oldN;
        NewName = newN;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
