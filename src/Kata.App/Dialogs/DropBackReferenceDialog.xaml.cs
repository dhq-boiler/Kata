using System.Linq;
using System.Windows;
using Kata.App.ViewModels;
using Kata.Core.Model;

namespace Kata.App.Dialogs;

public partial class DropBackReferenceDialog : Window
{
    public DropBackReferenceDialog(TypeNodeViewModel owner)
    {
        InitializeComponent();
        OwnerText.Text = owner.Ref.FullyQualifiedName;
        var rows = owner.Members
            .Where(m => m.Kind is MemberKind.Field or MemberKind.Property)
            .ToArray();
        FieldsList.ItemsSource = rows;
        if (rows.Length > 0) FieldsList.SelectedIndex = 0;
        OkButton.IsEnabled = rows.Length > 0;
    }

    public MemberItemViewModel? SelectedField { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (FieldsList.SelectedItem is MemberItemViewModel m)
        {
            SelectedField = m;
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
