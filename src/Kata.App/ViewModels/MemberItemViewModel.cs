using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Model;

namespace Kata.App.ViewModels;

public sealed partial class MemberItemViewModel : ObservableObject
{
    public MemberItemViewModel(MemberModel model)
    {
        Ref = model.Ref;
        _name = model.Name;
        _kind = model.Kind;
        _accessibility = model.Accessibility;
        _returnTypeDisplay = model.ReturnTypeDisplay;
        _isStatic = model.IsStatic;
        _isGhost = model.IsGhost;
        _isMacro = model.IsMacro;
        Smells = new ObservableCollection<CodeSmellViewModel>();
    }

    public MemberRef Ref { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private MemberKind _kind;
    [ObservableProperty] private MemberAccessibility _accessibility;
    [ObservableProperty] private string _returnTypeDisplay;
    [ObservableProperty] private bool _isStatic;
    [ObservableProperty] private bool _isGhost;
    [ObservableProperty] private bool _isMacro;
    [ObservableProperty] private DiffState _diffState = DiffState.Unchanged;

    public ObservableCollection<CodeSmellViewModel> Smells { get; }

    public bool HasSmells => Smells.Count > 0;
    public string SmellsTooltip => string.Join("\n", Smells.Select(s => s.TooltipLine));

    public void ApplySmells(IReadOnlyList<CodeSmell> smells)
    {
        Smells.Clear();
        foreach (var s in smells) Smells.Add(new CodeSmellViewModel(s));
        OnPropertyChanged(nameof(HasSmells));
        OnPropertyChanged(nameof(SmellsTooltip));
    }

    public string DisplayLine
    {
        get
        {
            var stereotype = IsMacro ? "«macro» " : string.Empty;
            var head = $"{Glyph(Accessibility)} {stereotype}{Name}{ParenSuffix()}";
            return string.IsNullOrEmpty(ReturnTypeDisplay)
                ? head
                : $"{head} : {ReturnTypeDisplay}";
        }
    }

    private string ParenSuffix()
    {
        // Only methods / constructors have a parameter list. Fields / properties / events
        // have a signature equal to the bare name (see SymbolKeyFormatter.FormatFieldSignature).
        if (Kind is not MemberKind.Method and not MemberKind.Constructor) return string.Empty;
        var sig = Ref.Signature;
        int open = sig.IndexOf('(');
        int close = sig.LastIndexOf(')');
        if (open < 0 || close <= open) return "()";
        return sig.Substring(open, close - open + 1);
    }

    private static string Glyph(MemberAccessibility a) => a switch
    {
        MemberAccessibility.Public => "+",
        MemberAccessibility.Private => "-",
        MemberAccessibility.Protected => "#",
        MemberAccessibility.Internal => "~",
        MemberAccessibility.ProtectedInternal => "#~",
        MemberAccessibility.PrivateProtected => "-#",
        _ => "?",
    };
}
