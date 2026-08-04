namespace Kata.Cpp.Syntax;

public sealed class CppSyntaxTree
{
    public string FilePath { get; }
    public string SourceText { get; }
    public IReadOnlyList<CppToken> Tokens { get; }
    public IReadOnlyList<CppDeclaration> Declarations { get; }

    private CppSyntaxTree(
        string filePath,
        string sourceText,
        IReadOnlyList<CppToken> tokens,
        IReadOnlyList<CppDeclaration> declarations)
    {
        FilePath = filePath;
        SourceText = sourceText;
        Tokens = tokens;
        Declarations = declarations;
    }

    public static CppSyntaxTree Parse(string filePath, string sourceText)
    {
        var tokens = CppCliLexer.Tokenize(sourceText);
        var decls = CppCliDeclParser.Parse(tokens);
        return new CppSyntaxTree(filePath, sourceText, tokens, decls);
    }

    public static CppSyntaxTree ParseFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return Parse(filePath, text);
    }
}
