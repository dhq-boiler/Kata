using Kata.Core.Model;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Tests;

public sealed class CppMemberResolutionTests
{
    private static CppCompilation CompileSingle(string filePath, string source)
        => CppCompilation.Create(new[] { CppSyntaxTree.Parse(filePath, source) });

    [Fact]
    public void ResolveMember_finds_directly_declared_method()
    {
        var comp = CompileSingle("NativeLib.h", """
            namespace NativeLib {
                public ref class ConnectionManager {
                public:
                    void Connect();
                };
            }
            """);
        var type = comp.GetTypeByFullyQualifiedName("NativeLib.ConnectionManager")!;

        var info = comp.ResolveMember(type, "Connect");

        Assert.Equal(CppCandidateReason.None, info.CandidateReason);
        Assert.NotNull(info.Symbol);
        Assert.Equal("Connect", info.Symbol!.Name);
        Assert.Same(type, info.Symbol.ContainingType);
        Assert.Equal(4, info.Symbol.DeclarationSite.Span.Line);
    }

    [Fact]
    public void ResolveMember_returns_not_found_for_missing_name()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo { public ref class Foo { public: void Bar(); }; }
            """);
        var type = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var info = comp.ResolveMember(type, "DoesNotExist");

        Assert.Equal(CppCandidateReason.NotFound, info.CandidateReason);
        Assert.Null(info.Symbol);
    }

    [Fact]
    public void ResolveMember_returns_ambiguous_when_overloads_exist_without_arity()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo {
                public ref class Foo {
                public:
                    void Do();
                    void Do(int x);
                    void Do(int x, int y);
                };
            }
            """);
        var type = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var info = comp.ResolveMember(type, "Do");

        Assert.Equal(CppCandidateReason.Ambiguous, info.CandidateReason);
        Assert.Null(info.Symbol);
        Assert.Equal(3, info.CandidateSymbols.Count);
    }

    [Fact]
    public void ResolveMember_narrows_overloads_by_arity()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo {
                public ref class Foo {
                public:
                    void Do();
                    void Do(int x);
                    void Do(int x, int y);
                };
            }
            """);
        var type = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var one = comp.ResolveMember(type, "Do", arity: 1);
        var two = comp.ResolveMember(type, "Do", arity: 2);
        var three = comp.ResolveMember(type, "Do", arity: 3);

        Assert.Equal(CppCandidateReason.None, one.CandidateReason);
        Assert.Single(one.Symbol!.Parameters);

        Assert.Equal(CppCandidateReason.None, two.CandidateReason);
        Assert.Equal(2, two.Symbol!.Parameters.Count);

        Assert.Equal(CppCandidateReason.NotFound, three.CandidateReason);
    }

    [Fact]
    public void ResolveMember_walks_into_base_type_when_not_declared_locally()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public ref class Base { public: void Ping(); };
                public ref class Derived : public Base {};
            }
            """);
        var derived = comp.GetTypeByFullyQualifiedName("demo.Derived")!;

        var info = comp.ResolveMember(derived, "Ping");

        Assert.Equal(CppCandidateReason.None, info.CandidateReason);
        Assert.NotNull(info.Symbol);
        Assert.Equal("demo.Base", info.Symbol!.ContainingType.FullyQualifiedName);
    }

    [Fact]
    public void ResolveMember_prefers_derived_declaration_over_base()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public ref class Base { public: virtual void Ping(); };
                public ref class Derived : public Base { public: virtual void Ping() override; };
            }
            """);
        var derived = comp.GetTypeByFullyQualifiedName("demo.Derived")!;

        var info = comp.ResolveMember(derived, "Ping");

        Assert.NotNull(info.Symbol);
        Assert.Equal("demo.Derived", info.Symbol!.ContainingType.FullyQualifiedName);
    }

    [Fact]
    public void ResolveMember_walks_interface_base_when_class_base_lacks_member()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public interface class IPing { void Ping(); };
                public ref class Impl : public System::Object, IPing { public: virtual void Pong(); };
            }
            """);
        var impl = comp.GetTypeByFullyQualifiedName("demo.Impl")!;

        var info = comp.ResolveMember(impl, "Ping");

        Assert.NotNull(info.Symbol);
        Assert.Equal("demo.IPing", info.Symbol!.ContainingType.FullyQualifiedName);
    }

    [Fact]
    public void ResolveMember_stops_walking_when_all_bases_are_external()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo {
                public ref class Foo : public System::Object { public: void Local(); };
            }
            """);
        var foo = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var info = comp.ResolveMember(foo, "ToString");

        // System::Object is not in the Cpp index, so its members are invisible.
        Assert.Equal(CppCandidateReason.NotFound, info.CandidateReason);
    }

    [Fact]
    public void ResolveMember_resolves_properties_events_and_fields()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo {
                public ref class Foo {
                public:
                    int Count;
                    property bool IsOn { bool get(); void set(bool value); }
                    event System::Action^ Changed;
                };
            }
            """);
        var foo = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        Assert.Equal(MemberKind.Field, comp.ResolveMember(foo, "Count").Symbol!.Kind);
        Assert.Equal(MemberKind.Property, comp.ResolveMember(foo, "IsOn").Symbol!.Kind);
        Assert.Equal(MemberKind.Event, comp.ResolveMember(foo, "Changed").Symbol!.Kind);
    }

    [Fact]
    public void ResolveMember_arity_is_ignored_for_non_callable_members()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo { public ref class Foo { public: int Count; }; }
            """);
        var foo = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var info = comp.ResolveMember(foo, "Count", arity: 5);

        Assert.NotNull(info.Symbol);
        Assert.Equal(MemberKind.Field, info.Symbol!.Kind);
    }

    [Fact]
    public void Members_expose_parsed_parameter_list()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo {
                public ref class Foo {
                public:
                    int Add(int x, int y);
                };
            }
            """);
        var add = comp.ResolveMember(
            comp.GetTypeByFullyQualifiedName("demo.Foo")!,
            "Add",
            arity: 2).Symbol!;

        Assert.Equal(2, add.Parameters.Count);
        Assert.Equal("x", add.Parameters[0].Name);
        Assert.Equal("int", add.Parameters[0].Type);
        Assert.Equal("y", add.Parameters[1].Name);
        Assert.Equal("int", add.Parameters[1].Type);
    }

    [Fact]
    public void ResolveMember_multi_level_inheritance_reaches_grandparent()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public ref class A { public: void Alpha(); };
                public ref class B : public A {};
                public ref class C : public B {};
            }
            """);
        var c = comp.GetTypeByFullyQualifiedName("demo.C")!;

        var info = comp.ResolveMember(c, "Alpha");

        Assert.NotNull(info.Symbol);
        Assert.Equal("demo.A", info.Symbol!.ContainingType.FullyQualifiedName);
    }
}
