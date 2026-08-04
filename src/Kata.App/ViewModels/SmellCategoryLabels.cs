using Kata.App.Localization;
using Kata.Core.Analysis;

namespace Kata.App.ViewModels;

// Localized display labels for Fowler's 24 smells. Reads from Strings.resx so
// switching UI language flips these too. Callers get the label for the
// current culture at the moment of access; changing culture mid-session and
// then re-opening the smell popup will pick up the new value.
internal static class SmellCategoryLabels
{
    public static string Localized(SmellCategory category) => category switch
    {
        SmellCategory.MysteriousName => Strings.Smell_Category_MysteriousName,
        SmellCategory.DuplicatedCode => Strings.Smell_Category_DuplicatedCode,
        SmellCategory.LongFunction => Strings.Smell_Category_LongFunction,
        SmellCategory.LongParameterList => Strings.Smell_Category_LongParameterList,
        SmellCategory.GlobalData => Strings.Smell_Category_GlobalData,
        SmellCategory.MutableData => Strings.Smell_Category_MutableData,
        SmellCategory.DivergentChange => Strings.Smell_Category_DivergentChange,
        SmellCategory.ShotgunSurgery => Strings.Smell_Category_ShotgunSurgery,
        SmellCategory.FeatureEnvy => Strings.Smell_Category_FeatureEnvy,
        SmellCategory.DataClumps => Strings.Smell_Category_DataClumps,
        SmellCategory.PrimitiveObsession => Strings.Smell_Category_PrimitiveObsession,
        SmellCategory.RepeatedSwitches => Strings.Smell_Category_RepeatedSwitches,
        SmellCategory.Loops => Strings.Smell_Category_Loops,
        SmellCategory.LazyElement => Strings.Smell_Category_LazyElement,
        SmellCategory.SpeculativeGenerality => Strings.Smell_Category_SpeculativeGenerality,
        SmellCategory.TemporaryField => Strings.Smell_Category_TemporaryField,
        SmellCategory.MessageChains => Strings.Smell_Category_MessageChains,
        SmellCategory.MiddleMan => Strings.Smell_Category_MiddleMan,
        SmellCategory.InsiderTrading => Strings.Smell_Category_InsiderTrading,
        SmellCategory.LargeClass => Strings.Smell_Category_LargeClass,
        SmellCategory.AlternativeClassesWithDifferentInterfaces => Strings.Smell_Category_AlternativeClassesWithDifferentInterfaces,
        SmellCategory.DataClass => Strings.Smell_Category_DataClass,
        SmellCategory.RefusedBequest => Strings.Smell_Category_RefusedBequest,
        SmellCategory.Comments => Strings.Smell_Category_Comments,
        _ => category.ToString(),
    };
}
