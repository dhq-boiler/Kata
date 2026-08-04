using Kata.Cpp;

namespace Kata.Tests;

public sealed class CppCliLexerPositionTests
{
    [Fact]
    public void Records_position_and_length_of_identifier()
    {
        const string source = "class Foo";
        var tokens = CppCliLexer.Tokenize(source);
        var foo = tokens.Single(t => t.Text == "Foo");

        Assert.Equal(6, foo.Position);
        Assert.Equal(3, foo.Length);
        Assert.Equal(1, foo.Line);
        Assert.Equal("Foo", source.Substring(foo.Position, foo.Length));
    }

    [Fact]
    public void Line_advances_across_newlines()
    {
        const string source = "class\nA\n{\n};";
        var tokens = CppCliLexer.Tokenize(source);

        Assert.Equal(1, tokens.Single(t => t.Text == "class").Line);
        Assert.Equal(2, tokens.Single(t => t.Text == "A").Line);
        Assert.Equal(3, tokens.Single(t => t.Text == "{").Line);
        Assert.Equal(4, tokens.First(t => t.Text == "}").Line);
    }

    [Fact]
    public void Line_comments_advance_line()
    {
        const string source = "// comment line 1\nclass A;";
        var tokens = CppCliLexer.Tokenize(source);
        var a = tokens.Single(t => t.Text == "A");

        Assert.Equal(2, a.Line);
    }

    [Fact]
    public void Block_comments_count_newlines_inside()
    {
        const string source = "/* multi\nline\ncomment */ class A;";
        var tokens = CppCliLexer.Tokenize(source);
        var a = tokens.Single(t => t.Text == "A");

        Assert.Equal(3, a.Line);
    }

    [Fact]
    public void String_literal_newlines_are_counted()
    {
        // NOTE: real C++ requires \ continuation to embed a newline, but the lexer
        // must remain robust to weird sources without desyncing line numbers.
        const string source = "\"multi\nline\" int x;";
        var tokens = CppCliLexer.Tokenize(source);
        var x = tokens.Single(t => t.Text == "x");

        Assert.Equal(2, x.Line);
    }

    [Fact]
    public void Preprocessor_directive_advances_line()
    {
        const string source = "#pragma once\n#include \"pch.h\"\nclass A;";
        var tokens = CppCliLexer.Tokenize(source);
        var a = tokens.Single(t => t.Text == "A");

        Assert.Equal(3, a.Line);
    }

    [Fact]
    public void Preprocessor_line_continuation_advances_line()
    {
        // The '#define' spans two physical lines via '\'.
        const string source = "#define FOO \\\n    bar\nclass A;";
        var tokens = CppCliLexer.Tokenize(source);
        var a = tokens.Single(t => t.Text == "A");

        Assert.Equal(3, a.Line);
    }

    [Fact]
    public void Punctuation_tokens_have_correct_position_and_line()
    {
        const string source = "class A\n{\n};";
        var tokens = CppCliLexer.Tokenize(source);

        var openBrace = tokens.First(t => t.Text == "{");
        Assert.Equal(8, openBrace.Position);
        Assert.Equal(1, openBrace.Length);
        Assert.Equal(2, openBrace.Line);
    }

    [Fact]
    public void End_of_file_token_line_is_final_line()
    {
        const string source = "class A;\nclass B;\n";
        var tokens = CppCliLexer.Tokenize(source);
        var eof = tokens.Last();

        Assert.Equal(CppTokenKind.EndOfFile, eof.Kind);
        // Position points past the source; final line is 3 (empty line after the trailing newline).
        Assert.Equal(source.Length, eof.Position);
        Assert.Equal(3, eof.Line);
    }
}
