using Kata.Core.Diff;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Tests;

public sealed class CppRenameEngineTests : IDisposable
{
    private readonly string _dir;

    public CppRenameEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kata-cpp-rename-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (string headerPath, string implPath, CppCompilation compilation) Load(string header, string impl)
    {
        var h = Path.Combine(_dir, "Foo.h");
        var c = Path.Combine(_dir, "Foo.cpp");
        File.WriteAllText(h, header);
        File.WriteAllText(c, impl);
        var comp = CppCompilation.Create(
            new[] { CppSyntaxTree.Parse(h, header) },
            new[] { CppSyntaxTree.Parse(c, impl) });
        return (h, c, comp);
    }

    [Fact]
    public void RenameType_updates_declaration_and_all_usages_in_header()
    {
        const string header = """
            namespace demo {
                public ref class Manager {
                    Handle^ _h;
                public:
                    Handle^ Connect(Source^ s);
                };
                public ref class Handle {};
                public ref class Source {};
            }
            """;
        const string impl = "";
        var (h, _, comp) = Load(header, impl);
        var manager = comp.GetTypeByFullyQualifiedName("demo.Manager")!;

        var changes = CppRenameEngine.RenameType(comp, manager, "Broker");

        var headerChange = Assert.Single(changes);
        Assert.Equal(h, headerChange.FilePath);
        Assert.Contains("class Broker", headerChange.NewText!);
        Assert.DoesNotContain("class Manager", headerChange.NewText!);
    }

    [Fact]
    public void RenameType_touches_multiple_files_when_referenced_across()
    {
        const string header = """
            namespace demo {
                public ref class Manager {};
                public ref class User {
                    Manager^ _m;
                };
            }
            """;
        const string impl = """
            void doIt(demo::Manager^ m) {
                demo::Manager^ local = m;
            }
            """;
        var (h, c, comp) = Load(header, impl);
        var manager = comp.GetTypeByFullyQualifiedName("demo.Manager")!;

        var changes = CppRenameEngine.RenameType(comp, manager, "Broker");

        Assert.Equal(2, changes.Count);
        var headerNew = changes.First(x => x.FilePath == h).NewText!;
        var implNew = changes.First(x => x.FilePath == c).NewText!;
        Assert.Contains("class Broker", headerNew);
        Assert.Contains("Broker^ _m", headerNew);
        Assert.Contains("Broker^ m", implNew);
        Assert.Contains("Broker^ local", implNew);
        Assert.DoesNotContain("Manager", headerNew);
        Assert.DoesNotContain("Manager", implNew);
    }

    [Fact]
    public void RenameMember_updates_declaration_impl_and_call_sites_by_arity()
    {
        const string header = """
            namespace demo {
                public ref class Manager {
                public:
                    void Connect(Source^ s, Destination^ d);
                };
                public ref class Source {
                public:
                    int Connect(Guid g, Pipeline^ p, LockFreeQueue^ q);
                };
            }
            """;
        const string impl = """
            void demo::Manager::Connect(demo::Source^ s, demo::Destination^ d) {
            }
            void user(demo::Manager^ mgr, demo::Source^ src) {
                mgr->Connect(a, b);
                src->Connect(g, p, q);
            }
            """;
        var (h, c, comp) = Load(header, impl);
        var manager = comp.GetTypeByFullyQualifiedName("demo.Manager")!;
        var connect = manager.Members.First(m => m.Name == "Connect");

        var changes = CppRenameEngine.RenameMember(comp, connect, "Link");

        // Both files touched.
        Assert.Equal(2, changes.Count);
        var headerNew = changes.First(x => x.FilePath == h).NewText!;
        var implNew = changes.First(x => x.FilePath == c).NewText!;
        // Manager::Connect renamed.
        Assert.Contains("void Link(Source^ s, Destination^ d);", headerNew);
        Assert.Contains("void demo::Manager::Link(demo::Source^ s, demo::Destination^ d)", implNew);
        // The mgr->Connect(a, b) call (arity 2) becomes mgr->Link(a, b).
        Assert.Contains("mgr->Link(a, b)", implNew);
        // Source::Connect (arity 3) untouched.
        Assert.Contains("int Connect(Guid g, Pipeline^ p, LockFreeQueue^ q);", headerNew);
        Assert.Contains("src->Connect(g, p, q)", implNew);
    }

    [Fact]
    public void RenameMember_field_updates_declaration_and_bare_access()
    {
        const string header = """
            namespace demo {
                public ref class Widget {
                public:
                    int state;
                };
            }
            """;
        const string impl = """
            void user(demo::Widget^ w) {
                int v = w->state;
                w->state = 42;
            }
            """;
        var (h, c, comp) = Load(header, impl);
        var widget = comp.GetTypeByFullyQualifiedName("demo.Widget")!;
        var stateMember = widget.Members.First(m => m.Name == "state");

        var changes = CppRenameEngine.RenameMember(comp, stateMember, "value");

        Assert.Equal(2, changes.Count);
        var headerNew = changes.First(x => x.FilePath == h).NewText!;
        var implNew = changes.First(x => x.FilePath == c).NewText!;
        Assert.Contains("int value;", headerNew);
        Assert.Contains("int v = w->value;", implNew);
        Assert.Contains("w->value = 42;", implNew);
        Assert.DoesNotContain("state", headerNew);
        Assert.DoesNotContain("->state", implNew);
    }

    [Fact]
    public void RenameParameter_updates_decl_impl_signatures_and_body_usage()
    {
        const string header = """
            namespace demo {
                public ref class Widget {
                public:
                    int Total(int a, int b);
                };
            }
            """;
        const string impl = """
            int demo::Widget::Total(int a, int b) {
                int t = a + b;
                if (a > 0) t = a * 2 + b;
                return t;
            }
            """;
        var (h, c, comp) = Load(header, impl);
        var widget = comp.GetTypeByFullyQualifiedName("demo.Widget")!;
        var total = widget.Members.First(m => m.Name == "Total");

        var changes = CppRenameEngine.RenameParameter(comp, total, "a", "alpha");

        Assert.Equal(2, changes.Count);
        var headerNew = changes.First(x => x.FilePath == h).NewText!;
        var implNew = changes.First(x => x.FilePath == c).NewText!;

        Assert.Contains("int Total(int alpha, int b);", headerNew);
        Assert.Contains("int demo::Widget::Total(int alpha, int b)", implNew);
        // body usage renamed
        Assert.Contains("int t = alpha + b;", implNew);
        Assert.Contains("if (alpha > 0) t = alpha * 2 + b;", implNew);
        // 'b' untouched
        Assert.Contains("+ b", implNew);
    }

    [Fact]
    public void RenameParameter_ignores_same_name_in_different_method()
    {
        const string header = """
            namespace demo {
                public ref class Widget {
                public:
                    int First(int a);
                    int Second(int a);
                };
            }
            """;
        const string impl = """
            int demo::Widget::First(int a) { return a + 1; }
            int demo::Widget::Second(int a) { return a * 2; }
            """;
        var (_, c, comp) = Load(header, impl);
        var widget = comp.GetTypeByFullyQualifiedName("demo.Widget")!;
        var first = widget.Members.First(m => m.Name == "First");

        var changes = CppRenameEngine.RenameParameter(comp, first, "a", "x");

        var implNew = changes.First(x => x.FilePath == c).NewText!;
        Assert.Contains("First(int x)", implNew);
        Assert.Contains("return x + 1;", implNew);
        // Second's parameter 'a' must NOT be renamed.
        Assert.Contains("Second(int a)", implNew);
        Assert.Contains("return a * 2;", implNew);
    }

    [Fact]
    public void RenameType_returns_empty_when_new_name_is_null_or_empty()
    {
        var (_, _, comp) = Load("namespace n { public ref class X {}; }", "");
        var x = comp.GetTypeByFullyQualifiedName("n.X")!;
        Assert.Empty(CppRenameEngine.RenameType(comp, x, ""));
    }
}
