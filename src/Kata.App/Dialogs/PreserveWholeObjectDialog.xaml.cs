using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class PreserveWholeObjectDialog : Window
{
    private readonly MemberItemViewModel[] _methods;
    private readonly IReadOnlyList<TypeNodeViewModel> _candidates;

    public PreserveWholeObjectDialog(TypeNodeViewModel owner, IReadOnlyList<TypeNodeViewModel> allTypes)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _methods = owner.Members.Where(m => m.Kind == MemberKind.Method).ToArray();
        foreach (var m in _methods) MethodCombo.Items.Add(m.DisplayLine);
        if (_methods.Length > 0) MethodCombo.SelectedIndex = 0;

        _candidates = allTypes
            .Where(t => t.Ref.FullyQualifiedName != owner.Ref.FullyQualifiedName)
            .OrderBy(t => t.Ref.FullyQualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var c in _candidates) ObjectTypeCombo.Items.Add(c.Ref.FullyQualifiedName);
        if (_candidates.Count > 0) ObjectTypeCombo.SelectedIndex = 0;

        OkButton.IsEnabled = _methods.Length > 0;
        Loaded += (_, _) => ReplacedBox.Focus();
    }

    public MemberItemViewModel? SelectedMethod { get; private set; }
    public TypeRef ObjectType { get; private set; }
    public string ParameterName { get; private set; } = "obj";
    public IReadOnlyList<string> ReplacedParams { get; private set; } = Array.Empty<string>();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MethodCombo.SelectedIndex < 0) return;
        var typeText = (ObjectTypeCombo.Text ?? string.Empty).Trim();
        var pname = ParamNameBox.Text.Trim();
        var replaced = ReplacedBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (typeText.Length == 0 || pname.Length == 0 || replaced.Length == 0) return;
        SelectedMethod = _methods[MethodCombo.SelectedIndex];
        ObjectType = new TypeRef(typeText);
        ParameterName = pname;
        ReplacedParams = replaced;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
