using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Kata.App.CodeViewer;

internal static class DarkCSharpTheme
{
    private static bool _applied;

    public static void ApplyOnce()
    {
        if (_applied) return;
        _applied = true;

        var def = HighlightingManager.Instance.GetDefinition("C#");
        if (def is null) return;

        var overrides = new (string Name, string Fg)[]
        {
            ("Comment",               "#6A9955"),
            ("String",                "#CE9178"),
            ("StringInterpolation",   "#CE9178"),
            ("Char",                  "#CE9178"),
            ("Preprocessor",          "#C586C0"),
            ("Punctuation",           "#D4D4D4"),
            ("ValueTypeKeywords",     "#4EC9B0"),
            ("ReferenceTypeKeywords", "#569CD6"),
            ("MethodCall",            "#DCDCAA"),
            ("NumberLiteral",         "#B5CEA8"),
            ("ThisOrBaseReference",   "#569CD6"),
            ("NullOrValueKeywords",   "#569CD6"),
            ("Keywords",              "#569CD6"),
            ("GotoKeywords",          "#C586C0"),
            ("ContextKeywords",       "#569CD6"),
            ("ExceptionKeywords",     "#C586C0"),
            ("CheckedKeyword",        "#569CD6"),
            ("UnsafeKeywords",        "#569CD6"),
            ("OperatorKeywords",      "#569CD6"),
            ("ParameterModifiers",    "#569CD6"),
            ("Modifiers",             "#569CD6"),
            ("Visibility",            "#569CD6"),
            ("NamespaceKeywords",     "#569CD6"),
            ("GetSetAddRemove",       "#569CD6"),
            ("TrueFalse",             "#569CD6"),
            ("TypeKeywords",          "#569CD6"),
            ("SemanticKeywords",      "#569CD6"),
            ("VarKeyword",            "#569CD6"),
        };

        foreach (var (name, fg) in overrides)
        {
            var color = def.GetNamedColor(name);
            if (color is null) continue;
            var mediaColor = (Color)ColorConverter.ConvertFromString(fg);
            color.Foreground = new SimpleHighlightingBrush(mediaColor);
        }
    }
}
