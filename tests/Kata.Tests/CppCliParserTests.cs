using Kata.Core.Model;
using Kata.Cpp;

namespace Kata.Tests;

public sealed class CppCliParserTests
{
    private static IReadOnlyList<CppDeclaration> ParseSource(string source)
    {
        var tokens = CppCliLexer.Tokenize(source);
        return CppCliDeclParser.Parse(tokens);
    }

    [Fact]
    public void Parses_simple_ref_class()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {};
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal("Foo", d.Name);
        Assert.Equal("demo", d.NamespaceFullName);
        Assert.Equal(TypeKind.Class, d.Kind);
    }

    [Fact]
    public void Skips_forward_declaration()
    {
        var decls = ParseSource("""
            namespace demo {
                ref class Fwd;
                public ref class Real {};
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal("Real", d.Name);
    }

    [Fact]
    public void Extracts_base_and_interfaces()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Derived : public Base, IDisposable, IComparable {};
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal("Derived", d.Name);
        Assert.Equal(new[] { "Base" }, d.BaseTypeNames);
        Assert.Equal(new[] { "IDisposable", "IComparable" }, d.InterfaceTypeNames);
    }

    [Fact]
    public void Handles_multi_level_namespace()
    {
        var decls = ParseSource("""
            namespace A::B::C {
                public ref class T {};
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal("A.B.C", d.NamespaceFullName);
    }

    [Fact]
    public void Distinguishes_kinds()
    {
        var decls = ParseSource("""
            namespace demo {
                public interface class IThing {};
                public value struct P {};
                public enum class Priority { Low, High };
                public ref class C {};
            }
            """);

        Assert.Equal(4, decls.Count);
        Assert.Equal(TypeKind.Interface, decls.Single(d => d.Name == "IThing").Kind);
        Assert.Equal(TypeKind.Struct, decls.Single(d => d.Name == "P").Kind);
        Assert.Equal(TypeKind.Enum, decls.Single(d => d.Name == "Priority").Kind);
        Assert.Equal(TypeKind.Class, decls.Single(d => d.Name == "C").Kind);
    }

    [Fact]
    public void Skips_preprocessor_and_comments()
    {
        var decls = ParseSource("""
            #pragma once
            #include "pch.h"

            // A comment mentioning ref class Fake
            /* another /* nested-ish */ ref class Faker;

            namespace demo {
                public ref class Real {};
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal("Real", d.Name);
    }

    [Fact]
    public void Skips_string_literal_with_class_word()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Real {
                    void M() { throw "ref class Fake in a string"; }
                };
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal("Real", d.Name);
    }

    [Fact]
    public void Method_and_field_are_extracted()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {
                public:
                    int Count;
                    void Reset();
                    static Foo^ Create(int size);
                };
            }
            """);

        var d = Assert.Single(decls);
        var count = d.Members.Single(m => m.Name == "Count");
        Assert.Equal(MemberKind.Field, count.Kind);
        Assert.Equal("int", count.ReturnTypeDisplay);
        Assert.Equal(MemberAccessibility.Public, count.Accessibility);

        var reset = d.Members.Single(m => m.Name == "Reset");
        Assert.Equal(MemberKind.Method, reset.Kind);
        Assert.Equal("void", reset.ReturnTypeDisplay);

        var create = d.Members.Single(m => m.Name == "Create");
        Assert.Equal(MemberKind.Method, create.Kind);
        Assert.True(create.IsStatic);
    }

    [Fact]
    public void Property_block_and_auto()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {
                public:
                    property bool IsOn { bool get(); void set(bool value); }
                    property int Count;
                };
            }
            """);

        var d = Assert.Single(decls);
        var isOn = d.Members.Single(m => m.Name == "IsOn");
        Assert.Equal(MemberKind.Property, isOn.Kind);
        Assert.Equal("bool", isOn.ReturnTypeDisplay);

        var count = d.Members.Single(m => m.Name == "Count");
        Assert.Equal(MemberKind.Property, count.Kind);
    }

    [Fact]
    public void Constructor_and_destructor()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {
                public:
                    Foo();
                    Foo(int size);
                    ~Foo();
                };
            }
            """);

        var d = Assert.Single(decls);
        var constructors = d.Members.Where(m => m.Kind == MemberKind.Constructor).ToList();
        Assert.Equal(2, constructors.Count);
        Assert.All(constructors, c => Assert.Equal("Foo", c.Name));
        // Destructor is intentionally skipped
        Assert.DoesNotContain(d.Members, m => m.Name == "Foo" && m.Kind == MemberKind.Method);
    }

    [Fact]
    public void Access_specifier_labels_are_tracked()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {
                public:
                    void Pub();
                private:
                    void Priv();
                protected:
                    void Pro();
                };
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal(MemberAccessibility.Public, d.Members.Single(m => m.Name == "Pub").Accessibility);
        Assert.Equal(MemberAccessibility.Private, d.Members.Single(m => m.Name == "Priv").Accessibility);
        Assert.Equal(MemberAccessibility.Protected, d.Members.Single(m => m.Name == "Pro").Accessibility);
    }

    [Fact]
    public void Enum_values_become_static_fields()
    {
        var decls = ParseSource("""
            namespace demo {
                public enum class Priority { Low, Medium = 5, High };
            }
            """);

        var d = Assert.Single(decls);
        Assert.Equal(TypeKind.Enum, d.Kind);
        Assert.Equal(new[] { "Low", "Medium", "High" }, d.Members.Select(m => m.Name).ToArray());
        Assert.All(d.Members, m => Assert.Equal(MemberKind.Field, m.Kind));
        Assert.All(d.Members, m => Assert.True(m.IsStatic));
        Assert.All(d.Members, m => Assert.Equal(MemberAccessibility.Public, m.Accessibility));
    }

    [Fact]
    public void Event_is_extracted()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {
                public:
                    event System::Action^ Changed;
                };
            }
            """);

        var d = Assert.Single(decls);
        var evt = Assert.Single(d.Members);
        Assert.Equal("Changed", evt.Name);
        Assert.Equal(MemberKind.Event, evt.Kind);
    }

    [Fact]
    public void Method_parameters_are_parsed()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class Foo {
                public:
                    void NoArgs();
                    int Add(int x, int y);
                    void Callback(System::Action^ handler, int retries = 3);
                    void Complex(array<Byte>^ data, System::Collections::Generic::List<int>^ items);
                };
            }
            """);

        var d = Assert.Single(decls);

        var noArgs = d.Members.Single(m => m.Name == "NoArgs");
        Assert.Empty(noArgs.Parameters ?? Array.Empty<CppParameter>());

        var add = d.Members.Single(m => m.Name == "Add");
        Assert.Collection(add.Parameters!,
            p => { Assert.Equal("int", p.Type); Assert.Equal("x", p.Name); },
            p => { Assert.Equal("int", p.Type); Assert.Equal("y", p.Name); });

        var cb = d.Members.Single(m => m.Name == "Callback");
        Assert.Collection(cb.Parameters!,
            p => { Assert.Equal("System :: Action ^", p.Type); Assert.Equal("handler", p.Name); },
            p => { Assert.Equal("int", p.Type); Assert.Equal("retries", p.Name); });

        var complex = d.Members.Single(m => m.Name == "Complex");
        Assert.Equal(2, complex.Parameters!.Count);
        Assert.Equal("data", complex.Parameters![0].Name);
        Assert.Equal("items", complex.Parameters![1].Name);
    }

    [Fact]
    public void Ref_class_default_access_is_private_but_ref_struct_is_public()
    {
        var decls = ParseSource("""
            namespace demo {
                public ref class ClsPriv { int a; };
                public ref struct StrPub { int b; };
            }
            """);

        var cls = decls.Single(d => d.Name == "ClsPriv");
        Assert.Equal(MemberAccessibility.Private, cls.Members.Single().Accessibility);

        var str = decls.Single(d => d.Name == "StrPub");
        Assert.Equal(MemberAccessibility.Public, str.Members.Single().Accessibility);
    }
}
