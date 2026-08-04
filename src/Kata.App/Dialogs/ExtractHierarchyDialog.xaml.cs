using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ExtractHierarchyDialog : Window
{
    public ExtractHierarchyDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        SubclassBox.Text = "Case1, Case2";

        // Populate the virtualize list from method members. Skip constructors
        // (they can't be abstract) and property accessors (users override
        // properties as a whole, not per-accessor from this dialog).
        var rows = new ObservableCollection<MethodRow>();
        foreach (var m in owner.Members)
        {
            if (m.Kind != MemberKind.Method) continue;
            if (m.Name == owner.Name) continue;
            rows.Add(new MethodRow(m.Ref, $"{m.DisplayLine}"));
        }
        MethodList.ItemsSource = rows;

        Loaded += (_, _) => { SubclassBox.Focus(); SubclassBox.SelectAll(); };
    }

    public IReadOnlyList<string> SubclassNames { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<MemberRef> MethodsToVirtualize { get; private set; } = Array.Empty<MemberRef>();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var names = SubclassBox.Text
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (names.Length == 0) return;

        SubclassNames = names;
        MethodsToVirtualize = MethodList.ItemsSource is IEnumerable<MethodRow> rows
            ? rows.Where(r => r.IsChecked).Select(r => r.Member).ToArray()
            : Array.Empty<MemberRef>();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class MethodRow : INotifyPropertyChanged
    {
        private bool _isChecked;
        public MethodRow(MemberRef member, string displayLabel)
        {
            Member = member;
            DisplayLabel = displayLabel;
        }
        public MemberRef Member { get; }
        public string DisplayLabel { get; }
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
