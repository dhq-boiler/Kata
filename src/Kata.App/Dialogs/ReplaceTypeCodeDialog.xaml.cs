using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ReplaceTypeCodeDialog : Window
{
    private readonly MemberItemViewModel[] _fields;

    public ReplaceTypeCodeDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _fields = owner.Members
            .Where(m => m.Kind is MemberKind.Field or MemberKind.Property)
            .ToArray();
        foreach (var f in _fields) FieldCombo.Items.Add(f.DisplayLine);
        if (_fields.Length > 0) FieldCombo.SelectedIndex = 0;
        NewClassBox.Text = owner.Name + "Code";
        CodesBox.Text = "Male=0\nFemale=1\nOther=2";
        OkButton.IsEnabled = _fields.Length > 0;
        Loaded += (_, _) => NewClassBox.Focus();
    }

    public MemberItemViewModel? SelectedField { get; private set; }
    public string NewClassName { get; private set; } = string.Empty;
    public string InnerType { get; private set; } = "int";
    public IReadOnlyList<TypeCodeEntry> Codes { get; private set; } = Array.Empty<TypeCodeEntry>();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (FieldCombo.SelectedIndex < 0) return;
        var newClass = NewClassBox.Text.Trim();
        var inner = InnerTypeBox.Text.Trim();
        if (string.IsNullOrEmpty(newClass) || string.IsNullOrEmpty(inner)) return;

        var parsed = new List<TypeCodeEntry>();
        foreach (var line in CodesBox.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var name = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (name.Length == 0 || val.Length == 0) continue;
            parsed.Add(new TypeCodeEntry(name, val));
        }
        if (parsed.Count == 0) return;

        SelectedField = _fields[FieldCombo.SelectedIndex];
        NewClassName = newClass;
        InnerType = inner;
        Codes = parsed;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
