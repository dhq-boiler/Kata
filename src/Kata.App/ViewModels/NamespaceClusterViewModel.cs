using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kata.App.ViewModels;

public sealed partial class NamespaceClusterViewModel : ObservableObject
{
    public NamespaceClusterViewModel(string namespaceName, Point location, Size size)
    {
        Namespace = namespaceName;
        _location = location;
        _size = size;
        var (background, border) = ColorFor(namespaceName);
        Background = background;
        BorderBrush = border;
    }

    public string Namespace { get; }

    [ObservableProperty] private Point _location;
    [ObservableProperty] private Size _size;

    public Brush Background { get; }
    public Brush BorderBrush { get; }

    private static (Brush Background, Brush Border) ColorFor(string namespaceName)
    {
        var hash = 2166136261u;
        foreach (var c in namespaceName)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        var hue = (hash % 360u) / 360.0;
        var bg = HsvToBrush(hue, 0.35, 0.28, alpha: 0.16);
        var border = HsvToBrush(hue, 0.55, 0.55, alpha: 0.55);
        bg.Freeze();
        border.Freeze();
        return (bg, border);
    }

    private static SolidColorBrush HsvToBrush(double h, double s, double v, double alpha)
    {
        var i = (int)(h * 6) % 6;
        var f = h * 6 - System.Math.Floor(h * 6);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        double r = 0, g = 0, b = 0;
        switch (i)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            case 5: r = v; g = p; b = q; break;
        }
        return new SolidColorBrush(Color.FromArgb(
            (byte)(alpha * 255),
            (byte)(r * 255),
            (byte)(g * 255),
            (byte)(b * 255)));
    }
}
