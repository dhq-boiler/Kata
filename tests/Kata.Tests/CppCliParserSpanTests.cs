using Kata.Core.Model;
using Kata.Cpp;

namespace Kata.Tests;

public sealed class CppCliParserSpanTests
{
    private static IReadOnlyList<CppDeclaration> ParseSource(string source)
    {
        var tokens = CppCliLexer.Tokenize(source);
        return CppCliDeclParser.Parse(tokens);
    }

    [Fact]
    public void Declaration_name_span_points_at_class_identifier()
    {
        const string source = """
            namespace demo {
                public ref class Foo {};
            }
            """;
        var decls = ParseSource(source);
        var foo = Assert.Single(decls);

        Assert.Equal("Foo", source.Substring(foo.NameSpan.Start, foo.NameSpan.Length));
        Assert.Equal(2, foo.NameSpan.Line);
    }

    [Fact]
    public void Declaration_name_span_reports_line_after_multiline_prelude()
    {
        const string source =
            "#pragma once\n" +
            "// header comment\n" +
            "namespace demo {\n" +
            "    public ref class Foo {};\n" +
            "}\n";
        var decls = ParseSource(source);
        var foo = Assert.Single(decls);

        Assert.Equal(4, foo.NameSpan.Line);
        Assert.Equal("Foo", source.Substring(foo.NameSpan.Start, foo.NameSpan.Length));
    }

    [Fact]
    public void Method_and_field_spans_point_to_their_identifiers()
    {
        const string source = """
            namespace demo {
                public ref class Foo {
                public:
                    int Count;
                    void Reset();
                };
            }
            """;
        var decls = ParseSource(source);
        var foo = Assert.Single(decls);

        var count = foo.Members.Single(m => m.Name == "Count");
        Assert.Equal("Count", source.Substring(count.NameSpan.Start, count.NameSpan.Length));
        Assert.Equal(4, count.NameSpan.Line);

        var reset = foo.Members.Single(m => m.Name == "Reset");
        Assert.Equal("Reset", source.Substring(reset.NameSpan.Start, reset.NameSpan.Length));
        Assert.Equal(5, reset.NameSpan.Line);
    }

    [Fact]
    public void Property_and_event_spans_point_to_their_identifiers()
    {
        const string source = """
            namespace demo {
                public ref class Foo {
                public:
                    property bool IsOn { bool get(); void set(bool value); }
                    event System::Action^ Changed;
                };
            }
            """;
        var decls = ParseSource(source);
        var foo = Assert.Single(decls);

        var isOn = foo.Members.Single(m => m.Name == "IsOn");
        Assert.Equal("IsOn", source.Substring(isOn.NameSpan.Start, isOn.NameSpan.Length));
        Assert.Equal(4, isOn.NameSpan.Line);

        var changed = foo.Members.Single(m => m.Name == "Changed");
        Assert.Equal("Changed", source.Substring(changed.NameSpan.Start, changed.NameSpan.Length));
        Assert.Equal(5, changed.NameSpan.Line);
    }

    [Fact]
    public void Enum_value_spans_point_to_the_enumerator()
    {
        const string source = """
            namespace demo {
                public enum class Priority { Low, Medium, High };
            }
            """;
        var decls = ParseSource(source);
        var priority = Assert.Single(decls);

        foreach (var m in priority.Members)
        {
            Assert.Equal(m.Name, source.Substring(m.NameSpan.Start, m.NameSpan.Length));
            Assert.Equal(2, m.NameSpan.Line);
        }
    }

    [Fact]
    public void Constructor_span_points_to_the_type_name()
    {
        const string source = """
            namespace demo {
                public ref class Foo {
                public:
                    Foo();
                    Foo(int size);
                };
            }
            """;
        var decls = ParseSource(source);
        var foo = Assert.Single(decls);

        var ctors = foo.Members.Where(m => m.Kind == MemberKind.Constructor).ToList();
        Assert.Equal(2, ctors.Count);
        Assert.All(ctors, c => Assert.Equal("Foo", source.Substring(c.NameSpan.Start, c.NameSpan.Length)));
        Assert.Equal(new[] { 4, 5 }, ctors.Select(c => c.NameSpan.Line).OrderBy(x => x).ToArray());
    }
}
