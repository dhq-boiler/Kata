using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Kata.App.Graph;
using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Model;

namespace Kata.App.ViewModels;

public sealed partial class TypeNodeViewModel : ObservableObject, IPositionedItem
{
    public TypeNodeViewModel(TypeModel model)
    {
        Ref = model.Ref;
        _name = model.Name;
        _namespace = model.Namespace;
        _kind = model.Kind;
        _accessibility = model.Accessibility;
        _isGhost = model.IsGhost;
        _isForeignProject = model.IsForeignProject;
        Members = new ObservableCollection<MemberItemViewModel>(
            model.Members.Select(m => new MemberItemViewModel(m)));
        TypeSmells = new ObservableCollection<CodeSmellViewModel>();
        _size = EstimateSize();
    }

    public TypeNodeViewModel(TypeRef externalRef, string displayName, NamespaceRef ns)
    {
        Ref = externalRef;
        _name = displayName;
        _namespace = ns;
        _kind = TypeKind.Unknown;
        _accessibility = MemberAccessibility.Public;
        _isExternal = true;
        Members = new ObservableCollection<MemberItemViewModel>();
        TypeSmells = new ObservableCollection<CodeSmellViewModel>();
        _size = EstimateSize();
    }

    private const double MinNodeWidth = 240;
    private const double MaxNodeWidth = 480;
    private const double HeaderHeight = 52;
    private const double MembersPadding = 12;
    private const double MemberRowHeight = 14;
    private const double NameCharWidth = 9;
    private const double NamespaceCharWidth = 6;
    private const double MemberCharWidth = 7.2;
    private const double SidePadding = 24;

    private Size EstimateSize()
    {
        var nameWidth = Name.Length * NameCharWidth + SidePadding;
        var nsWidth = Namespace.FullName.Length * NamespaceCharWidth + SidePadding;
        var memberWidth = 0.0;
        foreach (var m in Members)
        {
            var w = m.DisplayLine.Length * MemberCharWidth + SidePadding;
            if (w > memberWidth) memberWidth = w;
        }

        var contentWidth = Math.Max(nameWidth, Math.Max(nsWidth, memberWidth));
        var width = Math.Clamp(contentWidth, MinNodeWidth, MaxNodeWidth);
        var height = HeaderHeight + MembersPadding + Members.Count * MemberRowHeight;

        return new Size(width, height);
    }

    public TypeRef Ref { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private NamespaceRef _namespace;
    [ObservableProperty] private TypeKind _kind;
    [ObservableProperty] private MemberAccessibility _accessibility;
    [ObservableProperty] private bool _isGhost;
    [ObservableProperty] private bool _isExternal;
    [ObservableProperty] private bool _isForeignProject;
    [ObservableProperty] private Point _location;
    [ObservableProperty] private Size _size = new(240, 160);
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private DiffState _diffState = DiffState.Unchanged;

    // 接続矢印 hover / click 時に endpoint となる型をハイライトするためのフラグ。
    // ConnectionViewModel の同名プロパティと連動する transient state。
    [ObservableProperty] private bool _isEdgeHighlighted;

    public ObservableCollection<MemberItemViewModel> Members { get; }

    public ObservableCollection<CodeSmellViewModel> TypeSmells { get; }

    public bool HasTypeSmells => TypeSmells.Count > 0;
    public string TypeSmellsTooltip => string.Join("\n", TypeSmells.Select(s => s.TooltipLine));

    public void ApplySmells(SmellIndex index)
    {
        TypeSmells.Clear();
        foreach (var s in index.TypeOnly(Ref))
            TypeSmells.Add(new CodeSmellViewModel(s));

        foreach (var member in Members)
            member.ApplySmells(index.ForMember(member.Ref));

        OnPropertyChanged(nameof(HasTypeSmells));
        OnPropertyChanged(nameof(TypeSmellsTooltip));
    }

    public string KindLabel => IsForeignProject
        ? "«native»"
        : IsExternal
            ? "«external»"
            : Kind switch
            {
                TypeKind.Interface => "«interface»",
                TypeKind.Enum => "«enum»",
                TypeKind.Struct => "«struct»",
                TypeKind.Record => "«record»",
                TypeKind.Delegate => "«delegate»",
                _ => "«class»",
            };
}
