using Kata.Core.Analysis;

namespace Kata.App.ViewModels;

public sealed class CodeSmellViewModel
{
    public CodeSmellViewModel(CodeSmell smell)
    {
        Smell = smell;
    }

    public CodeSmell Smell { get; }
    public SmellCategory Category => Smell.Category;
    public SmellSeverity Severity => Smell.Severity;
    public string Message => Smell.Message;
    public string DisplayCategory => SmellCategoryLabels.Localized(Smell.Category);
    public string TooltipLine => $"💩 {DisplayCategory}: {Message}";
    public SmellFix? PrimaryFix => SmellRefactoringMap.Primary(Smell.Category, Smell.Member is not null);
    public bool HasFix => PrimaryFix is not null;
    public string FixLabel => PrimaryFix?.Label ?? string.Empty;
}
