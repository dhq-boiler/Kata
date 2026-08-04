using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class RemoveSettingMethodDialog : Window
{
    public RemoveSettingMethodDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;

        // Show properties (which typically have a setter) and any Set-prefixed methods.
        var rows = owner.Members
            .Where(m => m.Kind == MemberKind.Property || (m.Kind == MemberKind.Method && m.Name.StartsWith("Set", System.StringComparison.Ordinal)))
            .ToArray();
        MembersList.ItemsSource = rows;
        if (rows.Length > 0) MembersList.SelectedIndex = 0;
        OkButton.IsEnabled = rows.Length > 0;
    }

    public MemberItemViewModel? SelectedMember { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (MembersList.SelectedItem is MemberItemViewModel m)
        {
            SelectedMember = m;
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
