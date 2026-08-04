using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;
using Kata.Roslyn.HybridResolution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kata.Tests;

public sealed class HybridSymbolResolverTests
{
    private static CppCompilation CppFrom(string source, string path = "NativeLib.h")
        => CppCompilation.Create(new[] { CppSyntaxTree.Parse(path, source) });

    private static (SemanticModel Semantic, SyntaxTree Tree, Compilation Compilation) CompileCSharp(
        string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var comp = CSharpCompilation.Create(
            "Test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (comp.GetSemanticModel(tree), tree, comp);
    }

    private static MemberAccessExpressionSyntax FindMemberAccess(SyntaxTree tree, string memberName)
        => tree.GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .First(m => m.Name.Identifier.Text == memberName);

    [Fact]
    public void Resolves_field_access_when_receiver_type_lives_in_cpp_compilation()
    {
        var cpp = CppFrom("""
            namespace NativeLib {
                public ref class ConnectionManager {
                public:
                    void Connect();
                };
            }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                ConnectionManager _mgr;
                void Run() { _mgr.Connect(); }
            }
            """);
        var access = FindMemberAccess(tree, "Connect");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("NativeLib.ConnectionManager", result!.Type.FullyQualifiedName);
        Assert.Equal("Connect", result.Member.Name);
        Assert.Equal("NativeLib.h", result.Member.DeclarationSite.FilePath);
        Assert.Equal(4, result.Member.DeclarationSite.Span.Line);
        Assert.False(result.PreferTypeSite);
    }

    [Fact]
    public void Resolves_local_variable_receiver()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: void Draw(); }; }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                void Run() {
                    Widget w = null;
                    w.Draw();
                }
            }
            """);
        var access = FindMemberAccess(tree, "Draw");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Type.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_parameter_receiver()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: void Reset(); }; }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                void Run(Widget w) { w.Reset(); }
            }
            """);
        var access = FindMemberAccess(tree, "Reset");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Type.FullyQualifiedName);
        Assert.Equal("Reset", result.Member.Name);
    }

    [Fact]
    public void Uses_invocation_arity_to_disambiguate_overloads()
    {
        var cpp = CppFrom("""
            namespace demo {
                public ref class Widget {
                public:
                    void Set();
                    void Set(int x);
                    void Set(int x, int y);
                };
            }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                Widget _w;
                void Run() { _w.Set(1, 2); }
            }
            """);
        var access = FindMemberAccess(tree, "Set");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Member.Parameters.Count);
    }

    [Fact]
    public void Returns_null_when_receiver_type_is_unknown_to_cpp()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Known {}; }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                Unknown _u;
                void Run() { _u.Whatever(); }
            }
            """);
        var access = FindMemberAccess(tree, "Whatever");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.Null(result);
    }

    [Fact]
    public void Resolves_member_on_object_creation_receiver()
    {
        // new Widget().Draw() — the receiver is an ObjectCreationExpressionSyntax,
        // not an identifier. Fix G's InferCppType handles this by looking at the
        // ObjectCreation's Type syntax directly.
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: void Draw(); }; }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                void Run() { new Widget().Draw(); }
            }
            """);
        var access = FindMemberAccess(tree, "Draw");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Type.FullyQualifiedName);
        Assert.Equal("Draw", result.Member.Name);
    }

    [Fact]
    public void Returns_null_when_member_name_missing_on_resolved_type()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: void Draw(); }; }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                Widget _w;
                void Run() { _w.SomethingElse(); }
            }
            """);
        var access = FindMemberAccess(tree, "SomethingElse");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.Null(result);
    }

    [Fact]
    public void Resolves_member_on_var_local_initialized_by_cpp_invocation()
    {
        // handle is `var`, its initializer returns a Cpp type. handle.Disconnect()
        // must chain through: receiver var → initializer invocation → Cpp return type
        // → Disconnect on that Cpp type.
        var cpp = CppFrom("""
            namespace NativeLib {
                public ref class ConnectionHandle {
                public:
                    void Disconnect();
                };
                public ref class ConnectionManager {
                public:
                    ConnectionHandle^ Connect(int source, int destination);
                };
            }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                ConnectionManager _mgr;
                void Run() {
                    var handle = _mgr.Connect(1, 2);
                    handle.Disconnect();
                }
            }
            """);
        var access = FindMemberAccess(tree, "Disconnect");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("NativeLib.ConnectionHandle", result!.Type.FullyQualifiedName);
        Assert.Equal("Disconnect", result.Member.Name);
    }

    [Fact]
    public void Resolves_chained_invocation_receiver()
    {
        // _mgr.Connect(1,2).Disconnect() — nested invocation chain.
        var cpp = CppFrom("""
            namespace demo {
                public ref class Handle { public: void Close(); };
                public ref class Mgr { public: Handle^ Open(int x); };
            }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                Mgr _m;
                void Run() { _m.Open(1).Close(); }
            }
            """);
        var access = FindMemberAccess(tree, "Close");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("demo.Handle", result!.Type.FullyQualifiedName);
        Assert.Equal("Close", result.Member.Name);
    }

    [Fact]
    public void Resolves_captured_var_local_inside_lambda_body()
    {
        // handle captured by a lambda — same enclosing var local.
        var cpp = CppFrom("""
            namespace demo {
                public ref class Handle { public: void Kill(); };
                public ref class Mgr { public: Handle^ Open(); };
            }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            using System;
            class Host {
                Mgr _m;
                void Run() {
                    var handle = _m.Open();
                    Action a = () => handle.Kill();
                }
            }
            """);
        var access = FindMemberAccess(tree, "Kill");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("demo.Handle", result!.Type.FullyQualifiedName);
        Assert.Equal("Kill", result.Member.Name);
    }

    [Fact]
    public void Depth_limit_prevents_infinite_recursion()
    {
        // Pathological: var a = a.Foo(); would recurse forever without a depth cap.
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: Widget^ Foo(); void Bar(); }; }
            """);
        // Well-formed but deeply nested chain — verify the resolver survives.
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                Widget _w;
                void Run() { _w.Foo().Foo().Foo().Foo().Foo().Bar(); }
            }
            """);
        var access = FindMemberAccess(tree, "Bar");
        var resolver = new HybridSymbolResolver(cpp);

        // Chain depth exceeds MaxInferenceDepth — the outer .Bar() call falls off
        // the end of inference. We only assert we don't blow the stack / infinite-loop.
        var result = resolver.TryResolveMemberAccess(access, semantic);
        Assert.True(result is null || result.Member.Name == "Bar");
    }

    private static SimpleNameSyntax FindSimpleName(SyntaxTree tree, string text)
        => tree.GetRoot()
            .DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .First(n => n.Identifier.Text == text);

    [Fact]
    public void ResolveTypeName_lands_on_constructor_when_declared_by_cpp()
    {
        var cpp = CppFrom("""
            namespace NativeLib {
                public ref class ConnectionManager {
                public:
                    ConnectionManager();
                    void Connect();
                };
            }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host {
                ConnectionManager _mgr;
            }
            """);
        var typeName = FindSimpleName(tree, "ConnectionManager");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveTypeName(typeName);

        Assert.NotNull(result);
        Assert.Equal("NativeLib.ConnectionManager", result!.Type.FullyQualifiedName);
        Assert.Equal(Kata.Core.Model.MemberKind.Constructor, result.Member.Kind);
        Assert.True(result.PreferTypeSite);
    }

    [Fact]
    public void ResolveTypeName_lands_on_first_member_when_no_constructor()
    {
        var cpp = CppFrom("""
            namespace demo {
                public ref class Widget {
                public:
                    void Draw();
                    void Reset();
                };
            }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host { Widget _w; }
            """);
        var typeName = FindSimpleName(tree, "Widget");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveTypeName(typeName);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Type.FullyQualifiedName);
        Assert.Equal("Draw", result.Member.Name);
    }

    [Fact]
    public void ResolveTypeName_returns_null_when_type_has_no_members()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Empty {}; }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host { Empty _e; }
            """);
        var typeName = FindSimpleName(tree, "Empty");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveTypeName(typeName);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveTypeName_returns_null_for_unknown_type()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: void Draw(); }; }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host { Whatever _w; }
            """);
        var typeName = FindSimpleName(tree, "Whatever");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveTypeName(typeName);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveTypeName_resolves_ambiguous_by_first_candidate()
    {
        var t1 = CppSyntaxTree.Parse("a.h", """
            namespace A { public ref class Widget { public: void Draw(); }; }
            """);
        var t2 = CppSyntaxTree.Parse("b.h", """
            namespace B { public ref class Widget { public: void Draw(); }; }
            """);
        var cpp = CppCompilation.Create(new[] { t1, t2 });
        var (_, tree, _) = CompileCSharp("""
            class Host { Widget _w; }
            """);
        var typeName = FindSimpleName(tree, "Widget");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveTypeName(typeName);

        // Fallback picks the first candidate rather than giving up entirely —
        // gives the user *a* place to land while ambiguity is unresolved.
        Assert.NotNull(result);
        Assert.StartsWith("A.", result!.Type.FullyQualifiedName);
    }

    private static ImplicitObjectCreationExpressionSyntax FindImplicitNew(SyntaxTree tree)
        => tree.GetRoot()
            .DescendantNodes()
            .OfType<ImplicitObjectCreationExpressionSyntax>()
            .First();

    [Fact]
    public void ResolveImplicitObjectCreation_lands_on_constructor_via_field_type()
    {
        var cpp = CppFrom("""
            namespace NativeLib {
                public ref class ConnectionManager {
                public:
                    ConnectionManager();
                    void Connect();
                };
            }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host {
                ConnectionManager _mgr = new();
            }
            """);
        var implicitNew = FindImplicitNew(tree);
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveImplicitObjectCreation(implicitNew);

        Assert.NotNull(result);
        Assert.Equal("NativeLib.ConnectionManager", result!.Type.FullyQualifiedName);
        Assert.Equal(Kata.Core.Model.MemberKind.Constructor, result.Member.Kind);
        Assert.Empty(result.Member.Parameters);
        Assert.True(result.PreferTypeSite);
    }

    [Fact]
    public void ResolveImplicitObjectCreation_matches_constructor_arity()
    {
        var cpp = CppFrom("""
            namespace demo {
                public ref class Widget {
                public:
                    Widget();
                    Widget(int x);
                    Widget(int x, int y);
                };
            }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host {
                Widget _w = new(1, 2);
            }
            """);
        var implicitNew = FindImplicitNew(tree);
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveImplicitObjectCreation(implicitNew);

        Assert.NotNull(result);
        Assert.Equal(Kata.Core.Model.MemberKind.Constructor, result!.Member.Kind);
        Assert.Equal(2, result.Member.Parameters.Count);
    }

    [Fact]
    public void ResolveImplicitObjectCreation_via_local_variable_declaration()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Widget { public: Widget(); }; }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host {
                void Run() {
                    Widget w = new();
                }
            }
            """);
        var implicitNew = FindImplicitNew(tree);
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveImplicitObjectCreation(implicitNew);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Type.FullyQualifiedName);
    }

    [Fact]
    public void ResolveImplicitObjectCreation_returns_null_when_type_unknown_to_cpp()
    {
        var cpp = CppFrom("""
            namespace demo { public ref class Known { public: Known(); }; }
            """);
        var (_, tree, _) = CompileCSharp("""
            class Host { Unknown _u = new(); }
            """);
        var implicitNew = FindImplicitNew(tree);
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveImplicitObjectCreation(implicitNew);

        Assert.Null(result);
    }

    [Fact]
    public void Resolves_qualified_receiver_type_syntax()
    {
        var cpp = CppFrom("""
            namespace NativeLib {
                public ref class ConnectionManager { public: void Connect(); };
            }
            """);
        var (semantic, tree, _) = CompileCSharp("""
            class Host {
                NativeLib.ConnectionManager _mgr;
                void Run() { _mgr.Connect(); }
            }
            """);
        var access = FindMemberAccess(tree, "Connect");
        var resolver = new HybridSymbolResolver(cpp);

        var result = resolver.TryResolveMemberAccess(access, semantic);

        Assert.NotNull(result);
        Assert.Equal("NativeLib.ConnectionManager", result!.Type.FullyQualifiedName);
    }
}
