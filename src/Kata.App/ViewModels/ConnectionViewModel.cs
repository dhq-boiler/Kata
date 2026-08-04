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
    // Diff overlay 専用: member が Extract Class / Move Method 等で他型に
    // 移動したことを示す破線 edge。Routing は MSAGL を通さず、SolutionGraphBuilder が
    // node 位置確定後に直接 2 点 polyline で書く。
    Move,
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

    // Move edge 用の member 行 index (source 側 = 元 type 内での row 番号、
    // target 側 = 移動先 type 内での row 番号)。null は Move edge 以外の場合。
    public int? SourceMemberIndex { get; init; }
    public int? TargetMemberIndex { get; init; }
    // Move edge のラベル表示 (「«moved» MethodName」)。null は Move edge 以外の場合。
    public string? MoveLabel { get; init; }
    // Move edge が集約表示 (「«moved» 6 members」等) の場合、tooltip 用に個別 member 名を保持。
    public IReadOnlyList<string>? MoveMemberNames { get; init; }
    // tooltip 拡張用の改行込み詳細文字列。個別名なしなら空文字 (Run が何も表示しない)。
    public string MoveMemberDetail => MoveMemberNames is { Count: > 0 }
        ? "\n  • " + string.Join("\n  • ", MoveMemberNames)
        : string.Empty;

    public string Label => Kind switch
    {
        ConnectionKind.Interface => "implements",
        ConnectionKind.Inheritance => "extends",
        ConnectionKind.Uses => "uses",
        ConnectionKind.Move => MoveLabel ?? "moved",
        _ => string.Empty,
    };
}
