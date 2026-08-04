using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Kata.App.Graph;

// Public copies of Nodify's internal template converters.
// The NodifyEditor default template (Nodify/Themes/Styles/NodifyEditor.xaml)
// references these via {StaticResource ...}; since StaticResource inside a
// template can only resolve keys defined in the same ResourceDictionary (not
// cross-scope), and Nodify keeps its converter classes internal, we can't
// re-register the originals — we duplicate the trivial logic instead.
// Kept in lockstep with Nodify v7.3.0.

public sealed class NodifyUnscaleTransformConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (Transform)((TransformGroup)value).Children[0].Inverse!;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value;
}

public sealed class NodifyScaleDoubleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => (double)values[0] * (double)values[1];

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class NodifyScalePointConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => (Point)((Vector)(Point)values[0] * (double)values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
