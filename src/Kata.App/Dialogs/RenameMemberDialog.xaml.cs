using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class RenameMemberDialog : Window
{
    private readonly MemberItemViewModel[] _members;

    public RenameMemberDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;

        // Method / Property / Field / Event / Constructor — everything user-renamable.
        _members = owner.Members
            .Where(m => m.Kind is MemberKind.Method or MemberKind.Property or MemberKind.Field or MemberKind.Event)
            .ToArray();

        foreach (var m in _members) MemberCombo.Items.Add(FormatLabel(m));
        if (_members.Length > 0)
        {
            MemberCombo.SelectedIndex = 0;
            NewNameBox.Text = _members[0].Name;
            NewNameBox.SelectAll();
        }
        else
        {
            OkButton.IsEnabled = false;
        }
        Loaded += (_, _) => NewNameBox.Focus();
    }

    public MemberItemViewModel? SelectedMember { get; private set; }
    public string NewName { get; private set; } = string.Empty;

    private static string FormatLabel(MemberItemViewModel m) => $"[{m.Kind}] {m.Name}   {m.Ref.Signature}";

    private void OnMemberChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemberCombo.SelectedIndex >= 0 && MemberCombo.SelectedIndex < _members.Length)
        {
            NewNameBox.Text = _members[MemberCombo.SelectedIndex].Name;
            NewNameBox.SelectAll();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MemberCombo.SelectedIndex < 0) return;
        var newName = NewNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;
        var member = _members[MemberCombo.SelectedIndex];
        if (string.Equals(member.Name, newName, System.StringComparison.Ordinal)) return;
        SelectedMember = member;
        NewName = newName;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
