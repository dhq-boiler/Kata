using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class ExtractInterfaceDialog : Window
{
    private readonly ObservableCollection<MemberRow> _rows;

    public ExtractInterfaceDialog(TypeNodeViewModel node)
    {
        InitializeComponent();

        SourceClassText.Text = node.Ref.FullyQualifiedName;
        InterfaceNameBox.Text = "I" + node.Name;

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
            InterfaceNameBox.Focus();
            InterfaceNameBox.SelectAll();
        };
    }

    public string InterfaceName { get; private set; } = string.Empty;
    public IReadOnlyList<MemberItemViewModel> SelectedMembers { get; private set; } = System.Array.Empty<MemberItemViewModel>();

    private static bool IsExtractableMember(MemberItemViewModel member)
    {
        if (member.Accessibility != MemberAccessibility.Public)
        {
            return false;
        }

        return member.Kind is MemberKind.Method or MemberKind.Property or MemberKind.Event;
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
        var name = InterfaceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var chosen = _rows.Where(r => r.IsSelected).Select(r => r.Member).ToArray();
        if (chosen.Length == 0)
        {
            return;
        }

        InterfaceName = name;
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
