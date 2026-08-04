using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Tests;

public sealed class CppImplementationLocatorTests
{
    [Fact]
    public void Locates_out_of_class_method_definition()
    {
        const string source = """
            #include "ConnectionManager.h"

            namespace NativeLib {
                ConnectionHandle^ ConnectionManager::Connect(ISource^ src, IAudioDestination^ dst)
                {
                    return nullptr;
                }
            }
            """;

        var impls = CppImplementationLocator.Locate("ConnectionManager.cpp", source);
        var connect = Assert.Single(impls);

        Assert.Equal("ConnectionManager", connect.TypeName);
        Assert.Equal("Connect", connect.MethodName);
        Assert.Equal(2, connect.ArgumentCount);
        Assert.Equal("ConnectionManager.cpp", connect.FilePath);
        Assert.Equal("Connect", source.Substring(connect.MethodNameSpan.Start, connect.MethodNameSpan.Length));
    }

    [Fact]
    public void Locates_zero_argument_method()
    {
        const string source = """
            ConnectionManager::ConnectionManager()
            {
                m_lock = gcnew Object();
            }
            """;

        var impls = CppImplementationLocator.Locate("x.cpp", source);
        var ctor = Assert.Single(impls);
        Assert.Equal("ConnectionManager", ctor.MethodName);
        Assert.Equal(0, ctor.ArgumentCount);
    }

    [Fact]
    public void Ignores_declarations_without_body()
    {
        const string source = """
            ConnectionHandle^ ConnectionManager::Connect(ISource^ src);
            """;

        var impls = CppImplementationLocator.Locate("x.cpp", source);
        Assert.Empty(impls);
    }

    [Fact]
    public void Skips_constructor_initializer_list_then_finds_body()
    {
        const string source = """
            ConnectionManager::ConnectionManager(int x)
                : m_lock(gcnew Object()), m_count(x)
            {
            }
            """;

        var impls = CppImplementationLocator.Locate("x.cpp", source);
        var ctor = Assert.Single(impls);
        Assert.Equal("ConnectionManager", ctor.TypeName);
        Assert.Equal("ConnectionManager", ctor.MethodName);
        Assert.Equal(1, ctor.ArgumentCount);
    }

    [Fact]
    public void Records_line_number_of_method_name()
    {
        const string source =
            "// line 1\n" +
            "// line 2\n" +
            "namespace demo {\n" +
            "void Foo::Bar()\n" +
            "{\n" +
            "}\n" +
            "}\n";

        var impl = Assert.Single(CppImplementationLocator.Locate("x.cpp", source));
        Assert.Equal(4, impl.MethodNameSpan.Line);
    }

    [Fact]
    public void Multiple_definitions_in_one_file_are_all_reported()
    {
        const string source = """
            void Foo::A() { }
            void Foo::B(int x) { }
            void Foo::C(int x, int y) { }
            """;

        var impls = CppImplementationLocator.Locate("Foo.cpp", source);
        Assert.Equal(3, impls.Count);
        Assert.Equal(new[] { "A", "B", "C" }, impls.Select(i => i.MethodName).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, impls.Select(i => i.ArgumentCount).ToArray());
    }

    [Fact]
    public void Nested_scope_names_inside_body_are_not_reported()
    {
        const string source = """
            void Foo::Outer()
            {
                // These look like Type::Method but they are call expressions inside a body.
                Bar::Baz();
                Bar::Baz(1, 2);
            }
            """;

        var impls = CppImplementationLocator.Locate("x.cpp", source);
        var outer = Assert.Single(impls);
        Assert.Equal("Outer", outer.MethodName);
    }
}

public sealed class CppCompilationImplementationSiteTests
{
    [Fact]
    public void Attaches_implementation_site_from_paired_source()
    {
        var header = CppSyntaxTree.Parse("Foo.h", """
            namespace demo {
                public ref class Foo {
                public:
                    void Bar();
                };
            }
            """);
        var source = CppSyntaxTree.Parse("Foo.cpp", """
            #include "Foo.h"

            namespace demo {
                void Foo::Bar()
                {
                    // body
                }
            }
            """);

        var comp = CppCompilation.Create(new[] { header }, new[] { source });
        var bar = comp.GetTypeByFullyQualifiedName("demo.Foo")!.Members.Single(m => m.Name == "Bar");

        Assert.NotNull(bar.ImplementationSite);
        Assert.Equal("Foo.cpp", bar.ImplementationSite!.Value.FilePath);
        Assert.Equal(4, bar.ImplementationSite.Value.Span.Line);
    }

    [Fact]
    public void Overloaded_members_match_by_arity()
    {
        var header = CppSyntaxTree.Parse("Foo.h", """
            namespace demo {
                public ref class Foo {
                public:
                    void Do();
                    void Do(int x);
                    void Do(int x, int y);
                };
            }
            """);
        var source = CppSyntaxTree.Parse("Foo.cpp", """
            void Foo::Do() { }
            void Foo::Do(int x) { }
            void Foo::Do(int x, int y) { }
            """);

        var comp = CppCompilation.Create(new[] { header }, new[] { source });
        var members = comp.GetTypeByFullyQualifiedName("demo.Foo")!.Members
            .Where(m => m.Name == "Do")
            .OrderBy(m => m.Parameters.Count)
            .ToList();

        Assert.All(members, m => Assert.NotNull(m.ImplementationSite));
        // Each impl-site line differs — arity binding is 1:1, not "all point at the first".
        var lines = members.Select(m => m.ImplementationSite!.Value.Span.Line).ToArray();
        Assert.Equal(lines.Distinct().Count(), lines.Length);
    }

    [Fact]
    public void No_implementation_leaves_site_null()
    {
        var header = CppSyntaxTree.Parse("Foo.h", """
            namespace demo { public ref class Foo { public: void Bar(); }; }
            """);
        var comp = CppCompilation.Create(new[] { header });

        var bar = comp.GetTypeByFullyQualifiedName("demo.Foo")!.Members.Single();
        Assert.Null(bar.ImplementationSite);
    }

    [Fact]
    public void Ambiguous_type_name_skips_implementation_attachment()
    {
        // Two types named "Foo" in different namespaces → locator can't pick a winner.
        var h1 = CppSyntaxTree.Parse("A.h", """
            namespace A { public ref class Foo { public: void Bar(); }; }
            """);
        var h2 = CppSyntaxTree.Parse("B.h", """
            namespace B { public ref class Foo { public: void Bar(); }; }
            """);
        var source = CppSyntaxTree.Parse("shared.cpp", "void Foo::Bar() { }");

        var comp = CppCompilation.Create(new[] { h1, h2 }, new[] { source });
        var a = comp.GetTypeByFullyQualifiedName("A.Foo")!.Members.Single();
        var b = comp.GetTypeByFullyQualifiedName("B.Foo")!.Members.Single();

        Assert.Null(a.ImplementationSite);
        Assert.Null(b.ImplementationSite);
    }
}
