using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ConvertProceduralToObjectsDialog : Window
{
    public ConvertProceduralToObjectsDialog(
        TypeNodeViewModel proceduralNode,
        IReadOnlyList<TypeNodeViewModel> candidateRecordTypes)
    {
        InitializeComponent();
        ProceduralText.Text = proceduralNode.Ref.FullyQualifiedName;
        RecordCombo.ItemsSource = candidateRecordTypes;
        RecordCombo.DisplayMemberPath = nameof(TypeNodeViewModel.Name);
        if (candidateRecordTypes.Count > 0) RecordCombo.SelectedIndex = 0;

        var rows = new ObservableCollection<MethodRow>();
        foreach (var m in proceduralNode.Members)
        {
            if (m.Kind != MemberKind.Method) continue;
            if (!m.IsStatic) continue;
            rows.Add(new MethodRow(m.Ref, m.DisplayLine));
        }
        MethodList.ItemsSource = rows;

        Loaded += (_, _) => { RecordCombo.Focus(); };
    }

    public TypeNodeViewModel? SelectedRecord { get; private set; }
    public IReadOnlyList<MemberRef> MethodsToMove { get; private set; } = Array.Empty<MemberRef>();

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (RecordCombo.SelectedItem is not TypeNodeViewModel record) return;
        SelectedRecord = record;
        MethodsToMove = MethodList.ItemsSource is IEnumerable<MethodRow> rows
            ? rows.Where(r => r.IsChecked).Select(r => r.Member).ToArray()
            : Array.Empty<MemberRef>();
        if (MethodsToMove.Count == 0) return;
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
