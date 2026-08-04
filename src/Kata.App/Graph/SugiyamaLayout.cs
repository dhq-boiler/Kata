using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Kata.App.ViewModels;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Routing.Rectilinear;
using MsaglNode = Microsoft.Msagl.Core.Layout.Node;
using MsaglEdge = Microsoft.Msagl.Core.Layout.Edge;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;
using MsaglLine = Microsoft.Msagl.Core.Geometry.Curves.LineSegment;
using WpfPoint = System.Windows.Point;
using WpfLine = System.Windows.Media.LineSegment;

namespace Kata.App.Graph;

// Class-diagram layout via MSAGL Sugiyama, with an idempotent rebuild step for
// the edge geometry so it survives node-size changes.
//
// The layout runs on a background thread BEFORE any node has been rendered, so
// TypeNodeViewModel.Size is only an estimate (see EstimateSize in the VM). The
// estimate under-counts member row height in practice, which makes actual
// rendered nodes taller than what MSAGL saw — leaving MSAGL's edge endpoints
// embedded inside the class body. We fix that by keeping the raw polyline
// vertices as ConnectionViewModel.RoutePoints and re-deriving Geometry /
// EndPoint / EndAngle / LabelPosition from those + the live rects on every
// rebuild. Rebuilds are pure functions of (RoutePoints, sourceRect, targetRect)
// so they are idempotent — running Rebuild twice with the same inputs yields
// the same output, and simultaneous source/target resizes don't accumulate drift.
public static class SugiyamaLayout
{
    public static void Apply(
        IReadOnlyList<TypeNodeViewModel> nodes,
        IReadOnlyList<ConnectionViewModel> connections)
    {
        if (nodes.Count == 0)
        {
            return;
        }

        var graph = new GeometryGraph();
        var nodeMap = new Dictionary<TypeNodeViewModel, MsaglNode>(nodes.Count);
        var edgeMap = new Dictionary<MsaglEdge, ConnectionViewModel>(connections.Count);

        foreach (var vm in nodes)
        {
            var (w, h) = SizeOf(vm);
            var msaglNode = new MsaglNode(CurveFactory.CreateRectangle(w, h, new MsaglPoint()));
            graph.Nodes.Add(msaglNode);
            nodeMap[vm] = msaglNode;
        }

        foreach (var connection in connections)
        {
            if (nodeMap.TryGetValue(connection.SourceNode, out var src) &&
                nodeMap.TryGetValue(connection.TargetNode, out var tgt))
            {
                var edge = new MsaglEdge(src, tgt);
                graph.Edges.Add(edge);
                edgeMap[edge] = connection;
            }
        }

        var settings = new SugiyamaLayoutSettings
        {
            LayerSeparation = 120,
            NodeSeparation = 60,
            EdgeRoutingSettings =
            {
                EdgeRoutingMode = EdgeRoutingMode.Rectilinear,
                CornerRadius = 4,
            },
        };

        var layout = new LayeredLayout(graph, settings);
        layout.Run();

        var bbox = graph.BoundingBox;
        var offsetX = -bbox.Left;
        // MSAGL の Sugiyama は source を大きい Y、target を小さい Y に配置する。
        // UML の継承・実装は「基底(=target) が上、派生(=source) が下」で描きたいので、
        // WPF に写す際は Y をそのまま平行移動（下向きに増える）ようにする。
        var offsetY = -bbox.Bottom;

        foreach (var (vm, msaglNode) in nodeMap)
        {
            var c = msaglNode.Center;
            vm.Location = new WpfPoint(
                c.X - msaglNode.Width / 2 + offsetX,
                c.Y + offsetY - msaglNode.Height / 2);
        }

        foreach (var edge in graph.Edges)
        {
            if (!edgeMap.TryGetValue(edge, out var connection))
            {
                continue;
            }

            if (edge.Curve is null)
            {
                connection.Geometry = null;
                connection.RoutePoints = null;
                continue;
            }

            var route = ExtractRoutePoints(edge.Curve, offsetX, offsetY);
            connection.RoutePoints = route;
            RebuildFromRoute(connection);
        }
    }

    /// <summary>
    /// Route additional edges rectilinearly around already-placed nodes, without
    /// disturbing the node positions Apply() decided. Use this for edges that
    /// were intentionally excluded from the layered layout (e.g. Uses edges)
    /// so they still get the same rectilinear polyline styling as
    /// Inheritance/Interface edges.
    /// </summary>
    public static void RouteRectilinear(
        IReadOnlyList<TypeNodeViewModel> placedNodes,
        IReadOnlyList<ConnectionViewModel> extraEdges)
    {
        if (placedNodes.Count == 0 || extraEdges.Count == 0) return;

        var graph = new GeometryGraph();
        var nodeMap = new Dictionary<TypeNodeViewModel, MsaglNode>(placedNodes.Count);
        foreach (var vm in placedNodes)
        {
            var (w, h) = SizeOf(vm);
            // Node was already placed by Apply(); rebuild the MSAGL node at its
            // current WPF position so the router treats it as an obstacle at the
            // right coordinates.
            var center = new MsaglPoint(vm.Location.X + w / 2, vm.Location.Y + h / 2);
            var msaglNode = new MsaglNode(CurveFactory.CreateRectangle(w, h, center));
            graph.Nodes.Add(msaglNode);
            nodeMap[vm] = msaglNode;
        }

        var edgeMap = new Dictionary<MsaglEdge, ConnectionViewModel>(extraEdges.Count);
        foreach (var conn in extraEdges)
        {
            if (!nodeMap.TryGetValue(conn.SourceNode, out var src)) continue;
            if (!nodeMap.TryGetValue(conn.TargetNode, out var tgt)) continue;
            var edge = new MsaglEdge(src, tgt);
            graph.Edges.Add(edge);
            edgeMap[edge] = conn;
        }
        if (edgeMap.Count == 0) return;

        var router = new RectilinearEdgeRouter(graph, padding: 3, cornerFitRadius: 4, useSparseVisibilityGraph: true, useObstacleRectangles: true);
        router.Run();

        foreach (var edge in graph.Edges)
        {
            if (!edgeMap.TryGetValue(edge, out var conn)) continue;
            if (edge.Curve is null)
            {
                conn.Geometry = null;
                conn.RoutePoints = null;
                continue;
            }
            var route = ExtractRoutePoints(edge.Curve, 0, 0);
            conn.RoutePoints = route;
            RebuildFromRoute(conn);
        }
    }

    /// <summary>
    /// Re-derives Geometry / EndPoint / EndAngle / LabelPosition on the
    /// connection from its stored RoutePoints and the current source/target
    /// rectangles. Idempotent — call whenever a node's Size changes.
    /// Safe to call from the UI thread only (creates WPF Freezable objects).
    /// </summary>
    public static void RebuildFromRoute(ConnectionViewModel conn)
    {
        var route = conn.RoutePoints;
        if (route is null || route.Count < 2)
        {
            conn.Geometry = null;
            return;
        }

        var sourceRect = RectOf(conn.SourceNode);
        var targetRect = RectOf(conn.TargetNode);

        var pts = new List<WpfPoint>(route);
        TrimHead(pts, sourceRect);
        var (snappedEnd, endAngle) = TrimTail(pts, targetRect);

        if (pts.Count < 2)
        {
            conn.Geometry = null;
            return;
        }

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        for (int i = 1; i < pts.Count; i++)
        {
            figure.Segments.Add(new WpfLine(pts[i], true));
        }
        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        if (geom.CanFreeze) geom.Freeze();

        conn.Geometry = geom;
        conn.EndPoint = snappedEnd;
        conn.EndAngle = endAngle;
        conn.LabelPosition = ComputeLabelPosition(geom);
    }

    // Walk vertices from the source end. Consume every point strictly inside
    // sourceRect, then intersect the last-consumed → next-outside segment with
    // the source boundary and use that as the new pts[0]. This handles the
    // common case (source grew larger than the estimate, so multiple leading
    // vertices are now buried inside it) without leaving a stub inside the
    // node.
    private static void TrimHead(List<WpfPoint> pts, Rect sourceRect)
    {
        int firstOutside = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            if (!Contains(sourceRect, pts[i]))
            {
                firstOutside = i;
                break;
            }
        }

        if (firstOutside < 0)
        {
            // Route lives entirely inside source (degenerate self-nested case).
            return;
        }

        if (firstOutside == 0)
        {
            // Head is already outside. Snap it to the boundary using the
            // outgoing direction (pts[0] → pts[1]) so the line meets the
            // node's edge cleanly even if MSAGL's endpoint drifted.
            var next = pts.Count >= 2 ? pts[1] : pts[0];
            pts[0] = SnapExitPoint(sourceRect, pts[0], next);
            return;
        }

        var inside = pts[firstOutside - 1];
        var outside = pts[firstOutside];
        // IntersectSegmentWithRectBoundary takes (outside, inside) in that
        // order. The segment we're clipping runs from the source-INTERIOR
        // vertex to the first source-EXTERIOR vertex, so `outside` for the
        // helper is our local `outside` (the exterior point) and `inside`
        // is our local `inside` (the interior point). Passing them the other
        // way flips the direction vector's sign and picks the wrong side of
        // the rect — an inheritance edge exiting through the source's top
        // would then anchor to the source's bottom and traverse the entire
        // class body.
        var boundary = IntersectSegmentWithRectBoundary(sourceRect, outside, inside);
        pts.RemoveRange(0, firstOutside);
        pts.Insert(0, boundary);
    }

    // Mirror of TrimHead for the target end. Returns the final snapped endpoint
    // + the arrow angle to draw at it.
    private static (WpfPoint EndPoint, double AngleDegrees) TrimTail(
        List<WpfPoint> pts, Rect targetRect)
    {
        int lastOutside = -1;
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            if (!Contains(targetRect, pts[i]))
            {
                lastOutside = i;
                break;
            }
        }

        if (lastOutside < 0)
        {
            // Route lives entirely inside target.
            var end = pts[^1];
            return (end, 0);
        }

        if (lastOutside == pts.Count - 1)
        {
            // Tail hasn't reached target yet: snap the last vertex using the
            // arrival direction (pts[^2] → pts[^1]).
            var prev = pts.Count >= 2 ? pts[^2] : pts[^1];
            var (snapped, angle) = SnapEntryPoint(targetRect, pts[^1], prev);
            pts[^1] = snapped;
            return (snapped, angle);
        }

        var outside = pts[lastOutside];
        var inside = pts[lastOutside + 1];
        var (boundary, endAngle) = IntersectSegmentWithRectBoundaryAndAngle(targetRect, outside, inside);
        pts.RemoveRange(lastOutside + 1, pts.Count - (lastOutside + 1));
        pts.Add(boundary);
        return (boundary, endAngle);
    }

    // Snap a point sitting outside sourceRect to the rect boundary based on
    // the direction of travel toward the next route vertex — the edge exits
    // through the side matching the dominant axis of that direction.
    private static WpfPoint SnapExitPoint(Rect rect, WpfPoint at, WpfPoint next)
    {
        var dx = next.X - at.X;
        var dy = next.Y - at.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            dx = at.X - (rect.Left + rect.Width / 2);
            dy = at.Y - (rect.Top + rect.Height / 2);
        }
        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            var x = Math.Clamp(at.X, rect.Left, rect.Right);
            return new WpfPoint(x, dy > 0 ? rect.Bottom : rect.Top);
        }
        var y = Math.Clamp(at.Y, rect.Top, rect.Bottom);
        return new WpfPoint(dx > 0 ? rect.Right : rect.Left, y);
    }

    // Snap an entry point to the target's boundary and derive the arrow angle.
    // Direction of arrival = at - prev; the crossed side is opposite the
    // travel direction.
    private static (WpfPoint Point, double AngleDegrees) SnapEntryPoint(
        Rect rect, WpfPoint at, WpfPoint prev)
    {
        var dx = at.X - prev.X;
        var dy = at.Y - prev.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            dx = at.X - (rect.Left + rect.Width / 2);
            dy = at.Y - (rect.Top + rect.Height / 2);
        }
        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            var x = Math.Clamp(at.X, rect.Left, rect.Right);
            return dy > 0
                ? (new WpfPoint(x, rect.Top), 90)      // arrived from above
                : (new WpfPoint(x, rect.Bottom), -90); // arrived from below
        }
        var y = Math.Clamp(at.Y, rect.Top, rect.Bottom);
        return dx > 0
            ? (new WpfPoint(rect.Left, y), 0)     // arrived from left
            : (new WpfPoint(rect.Right, y), 180); // arrived from right
    }

    // Intersect a rectilinear-or-diagonal segment with the axis-aligned
    // boundary of a rect; the segment is assumed to have one endpoint outside
    // (or on) the boundary and the other inside.
    private static WpfPoint IntersectSegmentWithRectBoundary(Rect rect, WpfPoint outside, WpfPoint inside)
    {
        var dx = inside.X - outside.X;
        var dy = inside.Y - outside.Y;
        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            var y = dy > 0 ? rect.Top : rect.Bottom;
            var t = (y - outside.Y) / (Math.Abs(dy) < 0.001 ? 1 : dy);
            var x = outside.X + t * dx;
            x = Math.Clamp(x, rect.Left, rect.Right);
            return new WpfPoint(x, y);
        }
        else
        {
            var x = dx > 0 ? rect.Left : rect.Right;
            var t = (x - outside.X) / (Math.Abs(dx) < 0.001 ? 1 : dx);
            var y = outside.Y + t * dy;
            y = Math.Clamp(y, rect.Top, rect.Bottom);
            return new WpfPoint(x, y);
        }
    }

    private static (WpfPoint Point, double AngleDegrees) IntersectSegmentWithRectBoundaryAndAngle(
        Rect rect, WpfPoint outside, WpfPoint inside)
    {
        var boundary = IntersectSegmentWithRectBoundary(rect, outside, inside);
        var dx = inside.X - outside.X;
        var dy = inside.Y - outside.Y;
        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            return (boundary, dy > 0 ? 90 : -90);
        }
        return (boundary, dx > 0 ? 0 : 180);
    }

    // Rect.Contains uses half-open semantics — we want a point sitting
    // exactly on the boundary to be treated as OUTSIDE so we don't strip it
    // when trimming.
    private static bool Contains(Rect rect, WpfPoint p)
    {
        return p.X > rect.Left && p.X < rect.Right &&
               p.Y > rect.Top && p.Y < rect.Bottom;
    }

    private static (double W, double H) SizeOf(TypeNodeViewModel vm)
    {
        var w = vm.Size.Width > 0 ? vm.Size.Width : 240;
        var h = vm.Size.Height > 0 ? vm.Size.Height : 160;
        return (w, h);
    }

    private static Rect RectOf(TypeNodeViewModel vm)
    {
        var (w, h) = SizeOf(vm);
        return new Rect(vm.Location, new Size(w, h));
    }

    private static WpfPoint ComputeLabelPosition(PathGeometry geometry)
    {
        try
        {
            geometry.GetPointAtFractionLength(0.5, out var point, out _);
            return point;
        }
        catch
        {
            var b = geometry.Bounds;
            return new WpfPoint(b.X + b.Width / 2, b.Y + b.Height / 2);
        }
    }

    // Extract the polyline vertices from an MSAGL curve, applying the given
    // WPF-space offset. Bezier/Ellipse sub-curves are approximated by their
    // endpoint alone (rectilinear routing only uses tiny corner arcs, so this
    // gives a faithful polyline).
    private static IReadOnlyList<WpfPoint> ExtractRoutePoints(ICurve curve, double offsetX, double offsetY)
    {
        var pts = new List<WpfPoint>(16);
        pts.Add(ToWpf(curve.Start, offsetX, offsetY));
        AppendEndpoints(curve, offsetX, offsetY, pts);
        return pts;
    }

    private static void AppendEndpoints(ICurve curve, double offsetX, double offsetY, List<WpfPoint> pts)
    {
        if (curve is Curve composite)
        {
            foreach (var seg in composite.Segments)
            {
                AppendEndpoints(seg, offsetX, offsetY, pts);
            }
            return;
        }
        pts.Add(ToWpf(curve.End, offsetX, offsetY));
    }

    private static WpfPoint ToWpf(MsaglPoint p, double offsetX, double offsetY)
        => new WpfPoint(p.X + offsetX, p.Y + offsetY);
}
