using Kata.Core.Model;

namespace Kata.Tests;

public sealed class SymbolKeyFormatterTests
{
    [Theory]
    [InlineData("int", "int")]
    [InlineData("void", "void")]
    [InlineData("String^", "String")]
    [InlineData("System::Action^", "System.Action")]
    [InlineData("System :: Action ^", "System.Action")]
    [InlineData("Byte*", "Byte")]
    [InlineData("Widget&", "Widget")]
    [InlineData("NativeLib::ConnectionManager^", "NativeLib.ConnectionManager")]
    [InlineData("  int  ", "int")]
    [InlineData("", "")]
    public void NormalizeCppTypeName_matches_expected(string raw, string expected)
    {
        Assert.Equal(expected, SymbolKeyFormatter.NormalizeCppTypeName(raw));
    }

    [Fact]
    public void FormatMethodSignature_no_parameters()
    {
        var sig = SymbolKeyFormatter.FormatMethodSignature(
            "void", "Connect", Array.Empty<SymbolKeyFormatter.ParameterKey>());

        Assert.Equal("void Connect()", sig);
    }

    [Fact]
    public void FormatMethodSignature_with_primitive_parameters()
    {
        var sig = SymbolKeyFormatter.FormatMethodSignature(
            "int", "Add",
            new[]
            {
                new SymbolKeyFormatter.ParameterKey("int", "x"),
                new SymbolKeyFormatter.ParameterKey("int", "y"),
            });

        Assert.Equal("int Add(int x, int y)", sig);
    }

    [Fact]
    public void FormatMethodSignature_normalises_handle_parameters()
    {
        var sig = SymbolKeyFormatter.FormatMethodSignature(
            "void", "OnEvent",
            new[]
            {
                new SymbolKeyFormatter.ParameterKey("System :: Action ^", "handler"),
            });

        Assert.Equal("void OnEvent(System.Action handler)", sig);
    }

    [Fact]
    public void FormatMethodSignature_constructor_omits_return_type()
    {
        var sig = SymbolKeyFormatter.FormatMethodSignature(
            returnTypeDisplay: string.Empty,
            "Foo",
            new[] { new SymbolKeyFormatter.ParameterKey("int", "size") });

        Assert.Equal("Foo(int size)", sig);
    }

    [Fact]
    public void FormatFieldSignature_is_the_name()
    {
        Assert.Equal("Count", SymbolKeyFormatter.FormatFieldSignature("Count"));
    }
}
