using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ExtractSuperclassDialog : Window
{
    private readonly ObservableCollection<MemberRow> _rows;

    public ExtractSuperclassDialog(TypeNodeViewModel node)
    {
        InitializeComponent();

        SourceClassText.Text = node.Ref.FullyQualifiedName;
        SuperclassNameBox.Text = node.Name + "Base";

        _rows = new ObservableCollection<MemberRow>(
            node.Members
                .Where(IsExtractableMember)
                .Select(m => new MemberRow(m)));
        foreach (var row in _rows)
        {
            row.PropertyChanged += (_, _) => UpdateSelectionCount();
        }
        MembersList.ItemsSource = _rows;

        UpdateSelectionCount();

        Loaded += (_, _) =>
        {
            SuperclassNameBox.Focus();
            SuperclassNameBox.SelectAll();
        };
    }

    public string SuperclassName { get; private set; } = string.Empty;
    public IReadOnlyList<MemberItemViewModel> SelectedMembers { get; private set; } = System.Array.Empty<MemberItemViewModel>();

    private static bool IsExtractableMember(MemberItemViewModel member)
    {
        // Superclass can host anything non-private; keep it simple and match Extract Interface for now,
        // but also allow fields (they move up with implementation).
        return member.Accessibility is MemberAccessibility.Public or MemberAccessibility.Protected
            && member.Kind is MemberKind.Method or MemberKind.Property or MemberKind.Event or MemberKind.Field;
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
        var name = SuperclassNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var chosen = _rows.Where(r => r.IsSelected).Select(r => r.Member).ToArray();
        if (chosen.Length == 0)
        {
            return;
        }

        SuperclassName = name;
        SelectedMembers = chosen;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed class MemberRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public MemberRow(MemberItemViewModel member)
        {
            Member = member;
        }

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
