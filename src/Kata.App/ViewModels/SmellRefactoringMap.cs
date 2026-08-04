using Kata.App.Localization;
using Kata.Core.Analysis;

namespace Kata.App.ViewModels;

// Each of Fowler's 24 smells maps to one primary refactoring (Fowler ch.3 recommendations).
// The map returns null when there is no in-tool automation yet — the Popup then omits the
// "これで直す" button and shows only the description. Handler dispatch lives in
// MainWindow.OnSmellFixButtonClick — keeping this file free of any WPF/handler code so
// callers on other layers can also introspect the mapping.
public enum SmellRefactorKind
{
    None,
    Rename,
    RenameMember,
    ExtractMethod,
    ExtractClass,
    ExtractInterface,
    ExtractSuperclass,
    IntroduceParameterObject,
    EncapsulateField,
    RemoveSettingMethod,
    MoveMethod,
    ReplaceDataValueWithObject,
    ReplaceTypeCodeWithSubclasses,
    InlineMethod,
    CollapseHierarchy,
    PushDownMethod,
    // For body-level refactors (Extract Method / Inline Method / etc.) that need the user
    // to first open the code viewer and select a text range. The popup can't perform them
    // directly, so it just navigates to the source and prompts the user.
    OpenSourceForBodyRefactor,
}

public sealed record SmellFix(string Label, SmellRefactorKind Kind);

public static class SmellRefactoringMap
{
    public static SmellFix? Primary(SmellCategory category, bool hasMemberTarget) => category switch
    {
        SmellCategory.MysteriousName =>
            new SmellFix(hasMemberTarget ? Strings.Smell_Fix_RenameMember : Strings.Smell_Fix_RenameType,
                hasMemberTarget ? SmellRefactorKind.RenameMember : SmellRefactorKind.Rename),
        SmellCategory.DuplicatedCode => new SmellFix(Strings.Smell_Fix_OpenViewer_ExtractMethod, SmellRefactorKind.OpenSourceForBodyRefactor),
        SmellCategory.LongFunction => new SmellFix(Strings.Smell_Fix_OpenViewer_ExtractMethod, SmellRefactorKind.OpenSourceForBodyRefactor),
        SmellCategory.LongParameterList => new SmellFix(Strings.Smell_Fix_IntroduceParameterObject, SmellRefactorKind.IntroduceParameterObject),
        SmellCategory.GlobalData => new SmellFix(Strings.Smell_Fix_EncapsulateField, SmellRefactorKind.EncapsulateField),
        SmellCategory.MutableData => new SmellFix(Strings.Smell_Fix_RemoveSettingMethod, SmellRefactorKind.RemoveSettingMethod),
        SmellCategory.DivergentChange => new SmellFix(Strings.Smell_Fix_ExtractClass, SmellRefactorKind.ExtractClass),
        SmellCategory.ShotgunSurgery => new SmellFix(Strings.Smell_Fix_MoveMethod, SmellRefactorKind.MoveMethod),
        SmellCategory.FeatureEnvy => new SmellFix(Strings.Smell_Fix_MoveMethod, SmellRefactorKind.MoveMethod),
        SmellCategory.DataClumps => new SmellFix(Strings.Smell_Fix_IntroduceParameterObject, SmellRefactorKind.IntroduceParameterObject),
        SmellCategory.PrimitiveObsession => new SmellFix(Strings.Smell_Fix_ReplaceDataValueWithObject, SmellRefactorKind.ReplaceDataValueWithObject),
        SmellCategory.RepeatedSwitches => new SmellFix(Strings.Smell_Fix_ReplaceTypeCodeWithSubclasses, SmellRefactorKind.ReplaceTypeCodeWithSubclasses),
        SmellCategory.Loops => null, // No 1:1 automated refactoring in tool.
        SmellCategory.LazyElement => new SmellFix(Strings.Smell_Fix_OpenViewer_InlineMethod, SmellRefactorKind.OpenSourceForBodyRefactor),
        SmellCategory.SpeculativeGenerality => new SmellFix(Strings.Smell_Fix_CollapseHierarchy, SmellRefactorKind.CollapseHierarchy),
        SmellCategory.TemporaryField => new SmellFix(Strings.Smell_Fix_ExtractClass, SmellRefactorKind.ExtractClass),
        SmellCategory.MessageChains => new SmellFix(Strings.Smell_Fix_OpenViewer_ExtractMethod, SmellRefactorKind.OpenSourceForBodyRefactor),
        SmellCategory.MiddleMan => new SmellFix(Strings.Smell_Fix_OpenViewer_InlineMethod, SmellRefactorKind.OpenSourceForBodyRefactor),
        SmellCategory.InsiderTrading => new SmellFix(Strings.Smell_Fix_MoveMethod, SmellRefactorKind.MoveMethod),
        SmellCategory.LargeClass => new SmellFix(Strings.Smell_Fix_ExtractClass, SmellRefactorKind.ExtractClass),
        SmellCategory.AlternativeClassesWithDifferentInterfaces =>
            new SmellFix(Strings.Smell_Fix_ExtractSuperclass, SmellRefactorKind.ExtractSuperclass),
        SmellCategory.DataClass => new SmellFix(Strings.Smell_Fix_MoveMethod, SmellRefactorKind.MoveMethod),
        SmellCategory.RefusedBequest => new SmellFix(Strings.Smell_Fix_PushDownMethod, SmellRefactorKind.PushDownMethod),
        SmellCategory.Comments => new SmellFix(Strings.Smell_Fix_OpenViewer_ExtractMethod, SmellRefactorKind.OpenSourceForBodyRefactor),
        _ => null,
    };
}
