using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Kata.App.CodeViewer;

internal sealed class HoverBackgroundRenderer : IBackgroundRenderer
{
    public HoverBackgroundRenderer(Brush brush)
    {
        Brush = brush;
    }

    public Brush Brush { get; }

    public KnownLayer Layer => KnownLayer.Selection;

    public int? Start { get; private set; }
    public int? Length { get; private set; }

    public void SetRange(int? start, int? length)
    {
        Start = start;
        Length = length;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Start is null || Length is null || Length <= 0)
        {
            return;
        }
        var segment = new TextSegment { StartOffset = Start.Value, Length = Length.Value };
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            CornerRadius = 2,
        };
        builder.AddSegment(textView, segment);
        var geometry = builder.CreateGeometry();
        if (geometry is not null)
        {
            drawingContext.DrawGeometry(Brush, null, geometry);
        }
    }
}
