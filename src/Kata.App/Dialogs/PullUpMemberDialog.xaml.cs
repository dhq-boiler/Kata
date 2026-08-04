using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class PullUpMemberDialog : Window
{
    public enum MemberFilter { Methods, Fields }

    private readonly ObservableCollection<MemberRow> _rows;

    public PullUpMemberDialog(TypeNodeViewModel subclass, TypeRef parent, MemberFilter filter)
    {
        InitializeComponent();

        TitleText.Text = filter == MemberFilter.Methods
            ? "Pull up method"
            : "Pull up field";
        Title = TitleText.Text;
        SubclassText.Text = subclass.Ref.FullyQualifiedName;
        ParentText.Text = parent.FullyQualifiedName;

        _rows = new ObservableCollection<MemberRow>(
            subclass.Members
                .Where(m => Matches(m, filter))
                .Select(m => new MemberRow(m)));
        foreach (var row in _rows)
        {
            row.PropertyChanged += (_, _) => UpdateSelectionCount();
        }
        MembersList.ItemsSource = _rows;
        UpdateSelectionCount();
    }

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
        OkButton.IsEnabled = selected > 0;
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
        var chosen = _rows.Where(r => r.IsSelected).Select(r => r.Member).ToArray();
        if (chosen.Length == 0) return;
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
