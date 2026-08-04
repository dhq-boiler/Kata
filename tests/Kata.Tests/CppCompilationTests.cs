using Kata.Core.Model;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Tests;

public sealed class CppCompilationTests
{
    private static CppCompilation CompileSingle(string filePath, string source)
        => CppCompilation.Create(new[] { CppSyntaxTree.Parse(filePath, source) });

    [Fact]
    public void SyntaxTree_captures_path_source_tokens_and_declarations()
    {
        const string source = """
            namespace demo {
                public ref class Foo {};
            }
            """;
        var tree = CppSyntaxTree.Parse("Foo.h", source);

        Assert.Equal("Foo.h", tree.FilePath);
        Assert.Equal(source, tree.SourceText);
        Assert.NotEmpty(tree.Tokens);
        var foo = Assert.Single(tree.Declarations);
        Assert.Equal("Foo", foo.Name);
    }

    [Fact]
    public void Compilation_indexes_all_types_and_exposes_declaration_site()
    {
        var comp = CompileSingle("NativeLib.h", """
            namespace NativeLib {
                public ref class ConnectionManager {
                public:
                    void Connect();
                };
            }
            """);

        var symbol = Assert.Single(comp.AllTypes);
        Assert.Equal("NativeLib.ConnectionManager", symbol.FullyQualifiedName);
        Assert.Equal("NativeLib", symbol.NamespaceFullName);
        Assert.Equal("ConnectionManager", symbol.Name);
        Assert.Equal(TypeKind.Class, symbol.Kind);
        Assert.Equal("NativeLib.h", symbol.DeclarationSite.FilePath);
        Assert.Equal(2, symbol.DeclarationSite.Span.Line);
    }

    [Fact]
    public void ResolveType_finds_unique_type_by_simple_name()
    {
        var comp = CompileSingle("Foo.h", """
            namespace NativeLib {
                public ref class ConnectionManager {};
            }
            """);

        var info = comp.ResolveType("ConnectionManager");

        Assert.Equal(CppCandidateReason.None, info.CandidateReason);
        Assert.NotNull(info.Symbol);
        Assert.Equal("NativeLib.ConnectionManager", info.Symbol!.FullyQualifiedName);
    }

    [Fact]
    public void ResolveType_accepts_fully_qualified_name_with_dot_or_colon()
    {
        var comp = CompileSingle("Foo.h", """
            namespace A::B {
                public ref class T {};
            }
            """);

        var dotForm = comp.ResolveType("A.B.T");
        var colonForm = comp.ResolveType("A::B::T");

        Assert.NotNull(dotForm.Symbol);
        Assert.NotNull(colonForm.Symbol);
        Assert.Same(dotForm.Symbol, colonForm.Symbol);
    }

    [Fact]
    public void ResolveType_returns_ambiguous_when_two_namespaces_have_same_simple_name()
    {
        var tree1 = CppSyntaxTree.Parse("a.h", """
            namespace A {
                public ref class T {};
            }
            """);
        var tree2 = CppSyntaxTree.Parse("b.h", """
            namespace B {
                public ref class T {};
            }
            """);
        var comp = CppCompilation.Create(new[] { tree1, tree2 });

        var info = comp.ResolveType("T");

        Assert.Equal(CppCandidateReason.Ambiguous, info.CandidateReason);
        Assert.Null(info.Symbol);
        Assert.Equal(2, info.CandidateSymbols.Count);
        Assert.Contains(info.CandidateSymbols, s => s.FullyQualifiedName == "A.T");
        Assert.Contains(info.CandidateSymbols, s => s.FullyQualifiedName == "B.T");
    }

    [Fact]
    public void ResolveType_usings_disambiguate_before_simple_name_fallback()
    {
        var tree1 = CppSyntaxTree.Parse("a.h", """
            namespace A {
                public ref class T {};
            }
            """);
        var tree2 = CppSyntaxTree.Parse("b.h", """
            namespace B {
                public ref class T {};
            }
            """);
        var comp = CppCompilation.Create(new[] { tree1, tree2 });

        var info = comp.ResolveType("T", usings: new[] { "A" });

        Assert.Equal(CppCandidateReason.None, info.CandidateReason);
        Assert.NotNull(info.Symbol);
        Assert.Equal("A.T", info.Symbol!.FullyQualifiedName);
    }

    [Fact]
    public void ResolveType_returns_not_found_for_unknown_name()
    {
        var comp = CompileSingle("Foo.h", """
            namespace demo {
                public ref class Real {};
            }
            """);

        var info = comp.ResolveType("Nothing");

        Assert.Equal(CppCandidateReason.NotFound, info.CandidateReason);
        Assert.Null(info.Symbol);
        Assert.Empty(info.CandidateSymbols);
    }

    [Fact]
    public void Base_types_resolve_within_compilation()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public ref class Base {};
                public ref class Derived : public Base {};
            }
            """);

        var derived = comp.GetTypeByFullyQualifiedName("demo.Derived")!;
        Assert.NotNull(derived);

        var baseType = Assert.Single(derived.BaseTypes);
        Assert.Equal("demo.Base", baseType.FullyQualifiedName);
    }

    [Fact]
    public void Interface_bases_are_resolved_alongside_class_base()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public interface class IThing {};
                public interface class IOther {};
                public ref class Base {};
                public ref class Derived : public Base, IThing, IOther {};
            }
            """);

        var derived = comp.GetTypeByFullyQualifiedName("demo.Derived")!;

        Assert.Equal(3, derived.BaseTypes.Count);
        Assert.Contains(derived.BaseTypes, b => b.FullyQualifiedName == "demo.Base");
        Assert.Contains(derived.BaseTypes, b => b.FullyQualifiedName == "demo.IThing");
        Assert.Contains(derived.BaseTypes, b => b.FullyQualifiedName == "demo.IOther");
    }

    [Fact]
    public void Unresolved_bases_are_dropped_silently()
    {
        var comp = CompileSingle("Chain.h", """
            namespace demo {
                public ref class Derived : public System::Object {};
            }
            """);

        var derived = comp.GetTypeByFullyQualifiedName("demo.Derived")!;
        Assert.Empty(derived.BaseTypes);
    }

    [Fact]
    public void Member_symbols_flow_through_with_declaration_sites()
    {
        var comp = CompileSingle("NativeLib.h", """
            namespace NativeLib {
                public ref class ConnectionManager {
                public:
                    void Connect();
                    int Port;
                };
            }
            """);

        var symbol = comp.GetTypeByFullyQualifiedName("NativeLib.ConnectionManager")!;

        var connect = symbol.Members.Single(m => m.Name == "Connect");
        Assert.Equal(MemberKind.Method, connect.Kind);
        Assert.Equal("void Connect()", connect.Signature);
        Assert.Equal("NativeLib.h", connect.DeclarationSite.FilePath);
        Assert.Equal(4, connect.DeclarationSite.Span.Line);
        Assert.Same(symbol, connect.ContainingType);

        var port = symbol.Members.Single(m => m.Name == "Port");
        Assert.Equal(MemberKind.Field, port.Kind);
        Assert.Equal("Port", port.Signature);
    }

    [Fact]
    public void Compilation_from_multiple_trees_produces_all_types()
    {
        var a = CppSyntaxTree.Parse("A.h", """
            namespace X {
                public ref class A {};
            }
            """);
        var b = CppSyntaxTree.Parse("B.h", """
            namespace X {
                public ref class B {};
            }
            """);
        var comp = CppCompilation.Create(new[] { a, b });

        Assert.Equal(2, comp.SyntaxTrees.Count);
        Assert.Equal(2, comp.AllTypes.Count);
        Assert.NotNull(comp.GetTypeByFullyQualifiedName("X.A"));
        Assert.NotNull(comp.GetTypeByFullyQualifiedName("X.B"));
    }
}
