using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kata.App.Converters;

// Given (sourceLocation, sourceSize, targetLocation, targetSize) and
// a parameter "Source" | "Target", returns the Point where the edge line
// should meet the corresponding node — attached on the closest of the four
// sides (top/bottom/left/right) facing the other node.
public sealed class EdgeAnchorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 ||
            values[0] is not Point sourceLoc ||
            values[1] is not Size sourceSize ||
            values[2] is not Point targetLoc ||
            values[3] is not Size targetSize)
        {
            return new Point(0, 0);
        }

        var sourceRect = new Rect(sourceLoc, sourceSize);
        var targetRect = new Rect(targetLoc, targetSize);

        var wantSource = string.Equals(parameter as string, "Source", StringComparison.Ordinal);
        var self = wantSource ? sourceRect : targetRect;
        var other = wantSource ? targetRect : sourceRect;

        return ComputeAttach(self, other);
    }

    private static Point ComputeAttach(Rect self, Rect other)
    {
        var selfCenterX = self.Left + self.Width / 2;
        var selfCenterY = self.Top + self.Height / 2;
        var otherCenterX = other.Left + other.Width / 2;
        var otherCenterY = other.Top + other.Height / 2;

        var dx = otherCenterX - selfCenterX;
        var dy = otherCenterY - selfCenterY;

        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            return dy > 0
                ? new Point(selfCenterX, self.Bottom)
                : new Point(selfCenterX, self.Top);
        }

        return dx > 0
            ? new Point(self.Right, selfCenterY)
            : new Point(self.Left, selfCenterY);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
