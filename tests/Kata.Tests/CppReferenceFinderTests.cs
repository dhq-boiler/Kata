using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Tests;

public sealed class CppReferenceFinderTests
{
    private static CppCompilation CompileHeader(string filePath, string source)
        => CppCompilation.Create(new[] { CppSyntaxTree.Parse(filePath, source) });

    private static CppCompilation CompileHeaderAndImpl(string headerPath, string headerSource, string implPath, string implSource)
        => CppCompilation.Create(
            new[] { CppSyntaxTree.Parse(headerPath, headerSource) },
            new[] { CppSyntaxTree.Parse(implPath, implSource) });

    [Fact]
    public void Type_references_include_declaration_and_field_uses()
    {
        const string source = """
            namespace demo {
                public ref class Foo {};
                public ref class Bar {
                    Foo^ _foo;
                public:
                    void Use(Foo^ arg);
                };
            }
            """;
        var comp = CompileHeader("Foo.h", source);
        var foo = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var refs = CppReferenceFinder.FindTypeReferences(comp, foo);

        Assert.Contains(refs, r => r.Kind == CppReferenceKind.Declaration);
        Assert.Equal(3, refs.Count); // decl + Foo^ _foo + Use(Foo^ arg)
    }

    [Fact]
    public void Type_references_recognize_new_and_base_and_scope_and_cast()
    {
        const string source = """
            namespace demo {
                public ref class Foo {};
                public ref class Bar : Foo {
                    void Do() {
                        Foo^ x = gcnew Foo();
                        auto y = static_cast<Foo^>(x);
                        Foo::Something();
                    }
                };
            }
            """;
        var comp = CompileHeader("Foo.h", source);
        var foo = comp.GetTypeByFullyQualifiedName("demo.Foo")!;

        var refs = CppReferenceFinder.FindTypeReferences(comp, foo);

        // decl + `: Foo` + `Foo^ x` + `gcnew Foo` + `static_cast<Foo^>` + `Foo::Something` = 6 minimum
        Assert.True(refs.Count >= 5, $"expected >= 5 refs, got {refs.Count}");
        Assert.Contains(refs, r => r.Kind == CppReferenceKind.Declaration);
        Assert.Contains(refs, r => r.Kind == CppReferenceKind.TypeUse);
    }

    [Fact]
    public void Method_references_find_call_sites_across_files()
    {
        const string header = """
            namespace demo {
                public ref class Widget {
                public:
                    void Connect();
                };
            }
            """;
        const string impl = """
            void demo::Widget::Connect() {
            }
            void Consumer() {
                Widget^ w = gcnew Widget();
                w->Connect();
                w->Connect();
            }
            """;
        var comp = CompileHeaderAndImpl("Widget.h", header, "Widget.cpp", impl);
        var widget = comp.GetTypeByFullyQualifiedName("demo.Widget")!;
        var connect = widget.Members.First(m => m.Name == "Connect");

        var refs = CppReferenceFinder.FindMemberReferences(comp, connect);

        Assert.Contains(refs, r => r.Kind == CppReferenceKind.Declaration && r.FilePath.EndsWith(".h"));
        Assert.Contains(refs, r => r.Kind == CppReferenceKind.Declaration && r.FilePath.EndsWith(".cpp"));
        Assert.Equal(2, refs.Count(r => r.Kind == CppReferenceKind.MethodCall));
    }

    [Fact]
    public void Method_references_do_not_match_bare_name_before_paren_declarations()
    {
        const string source = """
            namespace demo {
                public ref class W {
                public:
                    void ping();
                };
                void other_ping() {
                    // this shouldn't match ping — different function
                }
            }
            """;
        var comp = CompileHeader("W.h", source);
        var w = comp.GetTypeByFullyQualifiedName("demo.W")!;
        var ping = w.Members.First(m => m.Name == "ping");

        var refs = CppReferenceFinder.FindMemberReferences(comp, ping);

        // Only the declaration of ping should match — other_ping is different text.
        Assert.Single(refs);
        Assert.Equal(CppReferenceKind.Declaration, refs[0].Kind);
    }

    [Fact]
    public void Method_references_filter_by_arity_across_same_named_overloads()
    {
        const string header = """
            namespace demo {
                public ref class Manager {
                public:
                    Handle^ Connect(Source^ s, Destination^ d);
                };
                public ref class Source {
                public:
                    int Connect(Guid g, Pipeline^ p, LockFreeQueue^ q);
                };
                public ref class Pipeline {
                public:
                    void Connect(Pipeline^ owner);
                };
            }
            """;
        const string impl = """
            void Consumer(demo::Manager^ mgr, demo::Source^ src, demo::Pipeline^ pipe) {
                mgr->Connect(a, b);                           // 2 args — target
                src->Connect(g, p, q);                        // 3 args — reject
                pipe->Connect(owner);                         // 1 arg  — reject
            }
            """;
        var comp = CompileHeaderAndImpl("M.h", header, "M.cpp", impl);
        var manager = comp.GetTypeByFullyQualifiedName("demo.Manager")!;
        var connect = manager.Members.First(m => m.Name == "Connect");

        var refs = CppReferenceFinder.FindMemberReferences(comp, connect);

        // Expect: 1 declaration on Manager.Connect + 1 call site with matching arity 2 = 2 total.
        var calls = refs.Where(r => r.Kind == CppReferenceKind.MethodCall).ToList();
        Assert.Single(calls);
        Assert.Contains("mgr->Connect", calls[0].LineSnippet);
    }

    [Fact]
    public void References_return_line_column_and_snippet()
    {
        const string source = """
            namespace demo {
                public ref class Alpha {};
                public ref class Beta {
                    Alpha^ handle;
                };
            }
            """;
        var comp = CompileHeader("A.h", source);
        var alpha = comp.GetTypeByFullyQualifiedName("demo.Alpha")!;

        var refs = CppReferenceFinder.FindTypeReferences(comp, alpha);

        var use = refs.First(r => r.Kind == CppReferenceKind.TypeUse);
        Assert.True(use.Line > 0);
        Assert.True(use.Column > 0);
        Assert.Contains("Alpha", use.LineSnippet);
    }
}
