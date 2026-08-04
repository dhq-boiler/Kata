using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class MoveMemberDialog : Window
{
    public enum MemberFilter { Methods, Fields }

    private readonly ObservableCollection<MemberRow> _rows;
    private readonly IReadOnlyList<TypeNodeViewModel> _candidates;

    public MoveMemberDialog(TypeNodeViewModel source, IReadOnlyList<TypeNodeViewModel> allTypes, MemberFilter filter)
    {
        InitializeComponent();

        TitleText.Text = filter == MemberFilter.Methods ? "Move method" : "Move field";
        Title = TitleText.Text;
        SourceText.Text = source.Ref.FullyQualifiedName;

        _candidates = allTypes
            .Where(t => t.Ref.FullyQualifiedName != source.Ref.FullyQualifiedName)
            .OrderBy(t => t.Ref.FullyQualifiedName, System.StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var c in _candidates) TargetCombo.Items.Add(c.Ref.FullyQualifiedName);
        if (_candidates.Count > 0) TargetCombo.SelectedIndex = 0;

        _rows = new ObservableCollection<MemberRow>(
            source.Members
                .Where(m => Matches(m, filter))
                .Select(m => new MemberRow(m)));
        foreach (var row in _rows)
        {
            row.PropertyChanged += (_, _) => UpdateSelectionCount();
        }
        MembersList.ItemsSource = _rows;
        UpdateSelectionCount();
    }

    public TypeRef SelectedTarget { get; private set; }
    public IReadOnlyList<MemberItemViewModel> SelectedMembers { get; private set; } = System.Array.Empty<MemberItemViewModel>();

    private static bool Matches(MemberItemViewModel member, MemberFilter filter)
    {
        return filter switch
        {
            MemberFilter.Methods => member.Kind is MemberKind.Method or MemberKind.Property,
            MemberFilter.Fields => member.Kind is MemberKind.Field,
            _ => false,
        };
    }

    private void UpdateSelectionCount()
    {
        var selected = _rows.Count(r => r.IsSelected);
        SelectionCountText.Text = $"{selected} / {_rows.Count} selected";
        OkButton.IsEnabled = selected > 0 && ResolveTargetRef() is not null;
    }

    private TypeRef? ResolveTargetRef()
    {
        var text = (TargetCombo.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var match = _candidates.FirstOrDefault(t =>
            string.Equals(t.Ref.FullyQualifiedName, text, System.StringComparison.Ordinal));
        return match?.Ref ?? new TypeRef(text);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = true;
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = false;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var target = ResolveTargetRef();
        if (target is null) return;
        var chosen = _rows.Where(r => r.IsSelected).Select(r => r.Member).ToArray();
        if (chosen.Length == 0) return;
        SelectedTarget = target.Value;
        SelectedMembers = chosen;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class MemberRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        public MemberRow(MemberItemViewModel member) { Member = member; }
        public MemberItemViewModel Member { get; }
        public string DisplayLine => Member.DisplayLine;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
