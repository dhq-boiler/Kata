using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ReplaceArrayWithObjectDialog : Window
{
    private readonly MemberItemViewModel[] _fields;

    public ReplaceArrayWithObjectDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        _fields = owner.Members
            .Where(m => m.Kind is MemberKind.Field or MemberKind.Property)
            .Where(m => m.ReturnTypeDisplay.Contains('[') || m.ReturnTypeDisplay.Contains("List"))
            .ToArray();
        // Fall back to all fields/properties if nothing looks array-ish.
        if (_fields.Length == 0)
        {
            _fields = owner.Members
                .Where(m => m.Kind is MemberKind.Field or MemberKind.Property)
                .ToArray();
        }
        foreach (var f in _fields) FieldCombo.Items.Add(f.DisplayLine);
        if (_fields.Length > 0) FieldCombo.SelectedIndex = 0;
        NewClassBox.Text = owner.Name + "Record";
        MappingsBox.Text = "0:Field0:string\n1:Field1:string\n2:Field2:string";
        OkButton.IsEnabled = _fields.Length > 0;
        Loaded += (_, _) => NewClassBox.Focus();
    }

    public MemberItemViewModel? SelectedField { get; private set; }
    public string NewClassName { get; private set; } = string.Empty;
    public IReadOnlyList<ArrayFieldMapping> Mappings { get; private set; } = Array.Empty<ArrayFieldMapping>();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (FieldCombo.SelectedIndex < 0) return;
        var newClass = NewClassBox.Text.Trim();
        if (newClass.Length == 0) return;

        var parsed = new List<ArrayFieldMapping>();
        foreach (var line in MappingsBox.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 3);
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0].Trim(), out var idx)) continue;
            var name = parts[1].Trim();
            var type = parts[2].Trim();
            if (name.Length == 0 || type.Length == 0) continue;
            parsed.Add(new ArrayFieldMapping(idx, name, type));
        }
        if (parsed.Count == 0) return;

        SelectedField = _fields[FieldCombo.SelectedIndex];
        NewClassName = newClass;
        Mappings = parsed;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
