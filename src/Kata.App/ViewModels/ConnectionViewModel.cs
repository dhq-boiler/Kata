using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kata.App.ViewModels;

public enum ConnectionKind
{
    Inheritance,
    Interface,
    Uses,
}

public sealed partial class ConnectionViewModel : ObservableObject
{
    public ConnectionViewModel(TypeNodeViewModel sourceNode, TypeNodeViewModel targetNode, ConnectionKind kind)
    {
        SourceNode = sourceNode;
        TargetNode = targetNode;
        _kind = kind;
    }

    public TypeNodeViewModel SourceNode { get; }
    public TypeNodeViewModel TargetNode { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private ConnectionKind _kind;
    [ObservableProperty] private PathGeometry? _geometry;
    [ObservableProperty] private Point _endPoint;
    [ObservableProperty] private double _endAngle;
    [ObservableProperty] private Point _labelPosition;
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private bool _isHighlighted;
    // Click 状態で「留める」ためのフラグ。マウスが外れても IsHighlighted は維持される。
    [ObservableProperty] private bool _isPinned;

    // The raw polyline vertices produced by MSAGL's edge routing (in WPF space,
    // offsets already applied). Kept as an immutable snapshot so we can
    // idempotently re-derive Geometry/EndPoint/EndAngle whenever a node's
    // actual rendered Size differs from the estimate used at layout time —
    // MSAGL runs on a background thread before any node is rendered, so its
    // endpoints land at the estimated node boundary; the actual rendered node
    // is usually taller, leaving the arrowhead embedded in the class body.
    public IReadOnlyList<Point>? RoutePoints { get; set; }

    public string Label => Kind switch
    {
        ConnectionKind.Interface => "implements",
        ConnectionKind.Inheritance => "extends",
        ConnectionKind.Uses => "uses",
        _ => string.Empty,
    };
}
