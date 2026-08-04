using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Nodify;

namespace Kata.App.Graph;

// Viewport-aware replacement for Nodify's NodifyCanvas.
//
// Design constraint: Nodify's NodifyEditor.ItemContainers walks
// `ItemContainerGenerator.ContainerFromIndex(i)` for every item and casts the
// result to ItemContainer — so any null (unrealized) entry crashes selection /
// dragging / hit-test. Full container virtualization is therefore off-limits
// with vanilla Nodify v7.3.0.
//
// What we CAN do without breaking Nodify: realize every container so the
// ItemContainers list stays non-null, but skip Measure() AND Arrange() for
// containers whose item is outside the viewport (padded by CachePadding).
// Measure is where ContentPresenter walks the DataTemplate and materializes
// the per-node visual subtree (border + header + members list + text runs),
// which is the actual cost — ~850ms for 461 nodes on 対象コードベース.
//
// Critical: WPF forces a Measure pass inside UIElement.Arrange() when
// IsMeasureValid is false. So Arrange must also be gated on the viewport
// check — arranging an unmeasured container would re-inflate its template
// and defeat the whole optimization (this was the cause of the 4.9s hitch
// after the initial fix). We use IsMeasureValid to detect skipped containers.
//
// Off-viewport containers therefore stay unmeasured and unarranged in the
// visual tree — invisible but present so Nodify's index-based lookups don't
// return null. When ViewportLocation changes, AffectsMeasure triggers a
// remeasure and formerly off-viewport containers materialize on demand.
public class VirtualizingNodifyCanvas : VirtualizingPanel
{
    public static readonly DependencyProperty ExtentProperty = DependencyProperty.Register(
        nameof(Extent), typeof(Rect), typeof(VirtualizingNodifyCanvas),
        new FrameworkPropertyMetadata(new Rect()));

    public Rect Extent
    {
        get => (Rect)GetValue(ExtentProperty);
        set => SetValue(ExtentProperty, value);
    }

    public static readonly DependencyProperty ViewportLocationProperty = DependencyProperty.Register(
        nameof(ViewportLocation), typeof(Point), typeof(VirtualizingNodifyCanvas),
        new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Point ViewportLocation
    {
        get => (Point)GetValue(ViewportLocationProperty);
        set => SetValue(ViewportLocationProperty, value);
    }

    public static readonly DependencyProperty ViewportSizeProperty = DependencyProperty.Register(
        nameof(ViewportSize), typeof(Size), typeof(VirtualizingNodifyCanvas),
        new FrameworkPropertyMetadata(default(Size), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Size ViewportSize
    {
        get => (Size)GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    public static readonly DependencyProperty CachePaddingProperty = DependencyProperty.Register(
        nameof(CachePadding), typeof(double), typeof(VirtualizingNodifyCanvas),
        new FrameworkPropertyMetadata(600.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double CachePadding
    {
        get => (double)GetValue(CachePaddingProperty);
        set => SetValue(CachePaddingProperty, value);
    }

    public static readonly DependencyProperty EstimatedItemSizeProperty = DependencyProperty.Register(
        nameof(EstimatedItemSize), typeof(Size), typeof(VirtualizingNodifyCanvas),
        new FrameworkPropertyMetadata(new Size(400, 400), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Size EstimatedItemSize
    {
        get => (Size)GetValue(EstimatedItemSizeProperty);
        set => SetValue(EstimatedItemSizeProperty, value);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // Generator has already cleared its realized-items list; drop
                // our stale InternalChildren so the next Measure regenerates
                // from scratch rather than duplicating containers.
                if (InternalChildren.Count > 0)
                    RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }

        // Nodify's NodifyEditor.ItemContainers walks ContainerFromIndex(i) for
        // every item WITHOUT a null-check and casts to ItemContainer. If a user
        // input event (mouse-down / hit-test) fires between here and the next
        // Measure pass, unrealized indices return null and SelectionHelper /
        // hit-test crash with NullReferenceException.
        //
        // Realize eagerly so ItemContainers is always fully populated. Measure
        // is still viewport-gated in MeasureOverride, so this doesn't
        // re-inflate the DataTemplate (that cost lives in Measure, not
        // realization).
        EnsureAllContainersRealized();
        InvalidateMeasure();
    }

    private void EnsureAllContainersRealized()
    {
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        if (itemsOwner is null) return;

        var generator = ItemContainerGenerator;
        var ownerGenerator = itemsOwner.ItemContainerGenerator;
        int itemCount = itemsOwner.Items.Count;

        for (int i = 0; i < itemCount; i++)
        {
            if (ownerGenerator.ContainerFromIndex(i) is not null) continue;

            var gpos = generator.GeneratorPositionFromIndex(i);
            using (generator.StartAt(gpos, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                if (generator.GenerateNext(out bool isNewlyRealized) is UIElement newChild)
                {
                    if (isNewlyRealized)
                        InsertInternalChild(InternalChildren.Count, newChild);
                    generator.PrepareItemContainer(newChild);
                }
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        if (itemsOwner is null) return default;

        // Touching this property triggers ItemContainerGenerator hydration so
        // OnItemsChanged fires.
        var generator = ItemContainerGenerator;
        var ownerGenerator = itemsOwner.ItemContainerGenerator;

        int itemCount = itemsOwner.Items.Count;
        if (itemCount == 0)
        {
            if (InternalChildren.Count > 0)
                RemoveInternalChildRange(0, InternalChildren.Count);
            Extent = new Rect();
            return default;
        }

        var vpSize = ViewportSize;
        bool useVirtualization = vpSize.Width > 0 && vpSize.Height > 0;
        Rect expanded = default;
        if (useVirtualization)
        {
            var pad = CachePadding;
            expanded = new Rect(
                ViewportLocation.X - pad,
                ViewportLocation.Y - pad,
                vpSize.Width + pad * 2,
                vpSize.Height + pad * 2);
        }

        var estimated = EstimatedItemSize;
        var childMeasureSize = new Size(double.PositiveInfinity, double.PositiveInfinity);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool haveExtent = false;

        for (int i = 0; i < itemCount; i++)
        {
            var item = itemsOwner.Items[i];
            if (item is null) continue;

            Point loc = default;
            Size size = estimated;
            bool hasKnownGeometry = false;
            if (item is IPositionedItem positioned)
            {
                loc = positioned.Location;
                var pSize = positioned.Size;
                if (pSize.Width > 0 && pSize.Height > 0) size = pSize;
                hasKnownGeometry = true;
            }

            // Realize every container (see class doc — Nodify's ItemContainers
            // enumerates ContainerFromIndex without a null check).
            var gpos = generator.GeneratorPositionFromIndex(i);
            bool isRealized = gpos.Offset == 0 && gpos.Index >= 0;
            UIElement? container;
            if (isRealized)
            {
                container = ownerGenerator.ContainerFromIndex(i) as UIElement;
            }
            else
            {
                container = null;
                using (generator.StartAt(gpos, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
                {
                    if (generator.GenerateNext(out bool isNewlyRealized) is UIElement newChild)
                    {
                        if (isNewlyRealized)
                            InsertInternalChild(InternalChildren.Count, newChild);
                        generator.PrepareItemContainer(newChild);
                        container = newChild;
                    }
                }
            }

            bool measureThisPass = !useVirtualization
                || !hasKnownGeometry
                || expanded.IntersectsWith(new Rect(loc, size));

            if (measureThisPass && container is not null)
            {
                container.Measure(childMeasureSize);
                var dSize = container.DesiredSize;
                if (dSize.Width > 0 && dSize.Height > 0)
                    size = dSize;
            }

            if (loc.X < minX) minX = loc.X;
            if (loc.Y < minY) minY = loc.Y;
            var right = loc.X + size.Width;
            var bottom = loc.Y + size.Height;
            if (right > maxX) maxX = right;
            if (bottom > maxY) maxY = bottom;
            haveExtent = true;
        }

        Extent = haveExtent
            ? new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY))
            : new Rect();

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            // Skip containers we didn't measure this pass — Arrange would
            // force a Measure and inflate the DataTemplate, wiping out the
            // viewport optimization.
            if (!child.IsMeasureValid) continue;
            if (child is INodifyCanvasItem item)
            {
                item.Arrange(new Rect(item.Location, item.DesiredSize));
            }
        }
        return finalSize;
    }
}
