using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Kata.App.ViewModels;

namespace Kata.App.Graph;

public static class NamespaceGridLayout
{
    private const double ColumnWidth = 320;
    private const double RowHeight = 240;
    private const double NamespaceHeader = 40;

    public static void Apply(IReadOnlyList<TypeNodeViewModel> nodes)
    {
        var byNamespace = nodes
            .GroupBy(n => n.Namespace.FullName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var columnIndex = 0;
        foreach (var group in byNamespace)
        {
            var rowIndex = 0;
            foreach (var node in group.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                node.Location = new Point(
                    columnIndex * ColumnWidth,
                    NamespaceHeader + rowIndex * RowHeight);
                rowIndex++;
            }
            columnIndex++;
        }
    }
}
