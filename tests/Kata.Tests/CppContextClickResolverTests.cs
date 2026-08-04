using Kata.Core.Model;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;
using Kata.Roslyn.HybridResolution;

namespace Kata.Tests;

public sealed class CppContextClickResolverTests
{
    private const string TypeSiteSignature = "<type>";

    private static (CppCompilation Cpp, string SourceText, string FilePath) BuildImplPair(
        string headerPath, string headerSource, string implPath, string implSource)
    {
        var headers = new[] { CppSyntaxTree.Parse(headerPath, headerSource) };
        var impls = new[] { CppSyntaxTree.Parse(implPath, implSource) };
        return (CppCompilation.Create(headers, impls), implSource, implPath);
    }

    private static int OffsetOf(string source, string needle)
    {
        var idx = source.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"needle '{needle}' not found in source");
        return idx;
    }

    [Fact]
    public void Resolves_click_on_return_type_in_implementation_file()
    {
        var header = """
            namespace demo {
                public ref class Handle { public: void Kill(); };
                public ref class Mgr { public: Handle^ Open(); };
            }
            """;
        var impl = """
            namespace demo {
                Handle^ Mgr::Open()
                {
                    return nullptr;
                }
            }
            """;
        var (cpp, implText, implPath) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);

        // Click position: on "Handle" in the return-type of the impl definition.
        // The first "Handle" in the impl text is the return type identifier.
        var offset = OffsetOf(implText, "Handle") + 1;

        // Host: viewer is currently showing Mgr::Open's implementation.
        var openMember = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Open");
        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("demo.Mgr"),
            openMember.Ref(),
            offset,
            TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Handle", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Equal(TypeSiteSignature, result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_click_on_containing_class_in_qualified_name()
    {
        var header = """
            namespace demo {
                public ref class Handle { public: void Kill(); };
                public ref class Mgr { public: Handle^ Open(); };
            }
            """;
        var impl = """
            namespace demo {
                Handle^ Mgr::Open()
                {
                    return nullptr;
                }
            }
            """;
        var (cpp, implText, implPath) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);

        // Click position: on "Mgr" in "Mgr::Open".
        var offset = OffsetOf(implText, "Mgr::Open") + 1;

        var openMember = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Open");
        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("demo.Mgr"),
            openMember.Ref(),
            offset,
            TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Mgr", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_click_on_parameter_type()
    {
        var header = """
            namespace demo {
                public ref class Src {};
                public ref class Dst {};
                public ref class Mgr { public: void Wire(Src^ s, Dst^ d); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Wire(Src^ s, Dst^ d)
                {
                }
            }
            """;
        var (cpp, implText, implPath) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);

        var wire = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Wire");

        // Click on "Dst" in the parameter list.
        var dstOffset = OffsetOf(implText, "Dst^") + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("demo.Mgr"),
            wire.Ref(),
            dstOffset,
            TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Dst", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Returns_null_when_click_lands_on_parameter_name()
    {
        var header = """
            namespace demo {
                public ref class Src {};
                public ref class Mgr { public: void Wire(Src^ s); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Wire(Src^ s)
                {
                }
            }
            """;
        var (cpp, implText, implPath) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var wire = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Wire");

        // "s" — parameter name, not a Cpp type.
        var offset = OffsetOf(implText, "^ s") + 2;
        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("demo.Mgr"),
            wire.Ref(),
            offset,
            TypeSiteSignature, out _);

        Assert.Null(result);
    }

    [Fact]
    public void Falls_back_to_header_when_no_implementation_exists()
    {
        // Header-only class. Viewer is showing the .h decl line. Ctrl+Click resolves within .h tokens.
        var header = """
            namespace demo {
                public ref class Handle {};
                public ref class Mgr {
                public:
                    Handle^ Open();
                };
            }
            """;
        var cpp = CppCompilation.Create(new[] { CppSyntaxTree.Parse("Mgr.h", header) });

        var open = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Open");
        var offset = OffsetOf(header, "Handle^ Open") + 1; // "Handle" identifier
        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("demo.Mgr"),
            open.Ref(),
            offset,
            TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Handle", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_click_on_property_getter_return_type()
    {
        // A property with an explicit accessor block. DeclParser skips the block,
        // but the Lexer still tokenises everything inside — so click positions
        // on getter/setter type identifiers should still resolve via token lookup.
        var header = """
            namespace demo {
                public ref class ISource {};
                public ref class Src {
                public:
                    property ISource^ Source { ISource^ get(); void set(ISource^ value); }
                };
            }
            """;
        var cpp = CppCompilation.Create(new[] { CppSyntaxTree.Parse("Src.h", header) });
        var prop = cpp.GetTypeByFullyQualifiedName("demo.Src")!.Members.Single(m => m.Name == "Source");

        // Second "ISource" occurrence = getter's return type inside the accessor block.
        var firstIdx = header.IndexOf("ISource", StringComparison.Ordinal);
        var secondIdx = header.IndexOf("ISource", firstIdx + 1, StringComparison.Ordinal);
        Assert.True(secondIdx > firstIdx);

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Src"), prop.Ref(), secondIdx + 1, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.ISource", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_click_on_property_setter_parameter_type()
    {
        var header = """
            namespace demo {
                public ref class ISource {};
                public ref class Src {
                public:
                    property ISource^ Source { ISource^ get(); void set(ISource^ value); }
                };
            }
            """;
        var cpp = CppCompilation.Create(new[] { CppSyntaxTree.Parse("Src.h", header) });
        var prop = cpp.GetTypeByFullyQualifiedName("demo.Src")!.Members.Single(m => m.Name == "Source");

        // Third "ISource" = setter's parameter type.
        var firstIdx = header.IndexOf("ISource", StringComparison.Ordinal);
        var secondIdx = header.IndexOf("ISource", firstIdx + 1, StringComparison.Ordinal);
        var thirdIdx = header.IndexOf("ISource", secondIdx + 1, StringComparison.Ordinal);
        Assert.True(thirdIdx > secondIdx);

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Src"), prop.Ref(), thirdIdx + 3, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.ISource", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_click_inside_type_declaration_view()
    {
        // Fix E flow: user first Ctrl+Clicked the type name, so contextMember carries
        // the <type> sentinel (viewer is showing the .h type declaration). Clicking on
        // an accessor's type inside that view must still resolve — anchor from the
        // type's DeclarationSite instead of trying to match a member.
        var header = """
            namespace demo {
                public ref class ISource {};
                public interface class IHost
                {
                    property ISource^ Parent
                    {
                        ISource^ get();
                        void set(ISource^ value);
                    }
                };
            }
            """;
        var cpp = CppCompilation.Create(new[] { CppSyntaxTree.Parse("IHost.h", header) });

        // Second "ISource" = getter return type inside the accessor block.
        var firstIdx = header.IndexOf("ISource", StringComparison.Ordinal);
        var secondIdx = header.IndexOf("ISource", firstIdx + 1, StringComparison.Ordinal);
        var thirdIdx = header.IndexOf("ISource", secondIdx + 1, StringComparison.Ordinal);
        Assert.True(thirdIdx > secondIdx);

        var typeSiteMember = new MemberRef(new TypeRef("demo.IHost"), TypeSiteSignature);

        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("demo.IHost"),
            typeSiteMember,
            thirdIdx + 1, // click inside the getter's return type
            TypeSiteSignature,
            out _);

        Assert.NotNull(result);
        Assert.Equal("demo.ISource", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_on_parameter_receiver()
    {
        // Inside Mgr::Wire(...)'s body, source->Foo() should resolve to Src::Foo.
        var header = """
            namespace demo {
                public ref class Src { public: void Foo(); };
                public ref class Mgr { public: void Wire(Src^ source); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Wire(Src^ source)
                {
                    source->Foo();
                }
            }
            """;
        var (cpp, implText, implPath) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var wire = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Wire");

        var offset = OffsetOf(implText, "source->Foo") + "source->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), wire.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Src", result!.Value.OwnerType.FullyQualifiedName);
        // Member ref should NOT be the type-site sentinel — this navigates to Foo, not Src's decl.
        Assert.NotEqual(TypeSiteSignature, result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_method_call_on_this_receiver()
    {
        var header = """
            namespace demo {
                public ref class Mgr {
                public:
                    void Wire();
                    void Bar();
                };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Wire()
                {
                    this->Bar();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var wire = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Wire");

        var offset = OffsetOf(implText, "this->Bar") + "this->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), wire.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Mgr", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Contains("Bar", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_method_call_on_host_type_field()
    {
        var header = """
            namespace demo {
                public ref class Widget { public: void Kill(); };
                public ref class Host {
                    Widget^ m_widget;
                public:
                    void Run();
                };
            }
            """;
        var impl = """
            namespace demo {
                void Host::Run()
                {
                    m_widget->Kill();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Host.h", header, "Host.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Host")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "m_widget->Kill") + "m_widget->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Host"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_with_arity_disambiguation()
    {
        var header = """
            namespace demo {
                public ref class Src {
                public:
                    void Do();
                    void Do(int x);
                    void Do(int x, int y);
                };
                public ref class Mgr { public: void Wire(Src^ source); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Wire(Src^ source)
                {
                    source->Do(1, 2);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var wire = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Wire");

        var offset = OffsetOf(implText, "source->Do") + "source->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), wire.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Src", result!.Value.OwnerType.FullyQualifiedName);
        // Signature encodes the 2-arg overload.
        Assert.Contains("int x, int y", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_property_style_call_without_parens()
    {
        var header = """
            namespace demo {
                public ref class Widget {
                public:
                    property int Count;
                };
                public ref class Host {
                    Widget^ m_widget;
                public:
                    void Run();
                };
            }
            """;
        var impl = """
            namespace demo {
                void Host::Run()
                {
                    auto c = m_widget->Count;
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Host.h", header, "Host.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Host")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "m_widget->Count") + "m_widget->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Host"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Equal("Count", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_method_call_on_explicit_local_var()
    {
        var header = """
            namespace demo {
                public ref class AudioPipeline { public: void Initialize(); };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    AudioPipeline^ pipeline = gcnew AudioPipeline();
                    pipeline->Initialize();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "pipeline->Initialize") + "pipeline->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.AudioPipeline", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_on_auto_gcnew_local_var()
    {
        var header = """
            namespace demo {
                public ref class AudioPipeline { public: void Initialize(); };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    auto pipeline = gcnew AudioPipeline();
                    pipeline->Initialize();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "pipeline->Initialize") + "pipeline->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.AudioPipeline", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_on_local_var_visible_from_nested_try_block()
    {
        // Real-world C++/CLI pattern: `auto pipeline = ...;` declared in the enclosing
        // scope, `pipeline->Initialize();` called inside a nested try{} block.
        // Backward scan must skip past the enclosing scope opener without giving up.
        var header = """
            namespace demo {
                public ref class AudioPipeline { public: void Initialize(); };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    auto pipeline = gcnew AudioPipeline();
                    try
                    {
                        pipeline->Initialize();
                    }
                    catch (int e)
                    {
                    }
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "pipeline->Initialize") + "pipeline->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.AudioPipeline", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Does_not_leak_local_var_from_sibling_prior_block()
    {
        // A local `pipeline` declared inside a sibling if{} block that already closed
        // must NOT be captured as the receiver's type for a later statement.
        var header = """
            namespace demo {
                public ref class AudioPipeline { public: void Initialize(); };
                public ref class OtherThing { public: void Initialize(); };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    if (true)
                    {
                        AudioPipeline^ pipeline = gcnew AudioPipeline();
                    }
                    OtherThing^ pipeline = gcnew OtherThing();
                    pipeline->Initialize();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "pipeline->Initialize") + "pipeline->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        // Should resolve to OtherThing (the later, in-scope declaration), NOT AudioPipeline.
        Assert.Equal("demo.OtherThing", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_bare_method_call_as_implicit_this()
    {
        // `IsFirstConnectionTo(destination);` inside ConnectionManager::Connect —
        // no explicit receiver, calls the enclosing type's own member.
        var header = """
            namespace demo {
                public ref class Dest {};
                public ref class Mgr {
                public:
                    void Run();
                private:
                    bool IsFirstConnectionTo(Dest^ destination);
                };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    Dest^ destination = nullptr;
                    bool isFirst = IsFirstConnectionTo(destination);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "IsFirstConnectionTo(destination)") + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Mgr", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Contains("IsFirstConnectionTo", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_method_call_on_auto_from_implicit_this_method_return()
    {
        // Real-world C++/CLI pattern: `auto strategy = ResolveStrategy(source, destination);`
        // where ResolveStrategy is a host-type method returning IConnectionStrategy^.
        // Then `strategy->ConfigurePipeline(...)` must resolve to IConnectionStrategy::ConfigurePipeline.
        var header = """
            namespace demo {
                public ref class Src {};
                public ref class Dst {};
                public interface class IConnectionStrategy {
                public:
                    virtual void ConfigurePipeline(Src^ s, Dst^ d) = 0;
                };
                public ref class Mgr {
                public:
                    void Run(Src^ source, Dst^ destination);
                private:
                    IConnectionStrategy^ ResolveStrategy(Src^ source, Dst^ destination);
                };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Src^ source, Dst^ destination)
                {
                    auto strategy = ResolveStrategy(source, destination);
                    strategy->ConfigurePipeline(source, destination);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "strategy->ConfigurePipeline") + "strategy->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.IConnectionStrategy", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Contains("ConfigurePipeline", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_method_call_on_auto_from_receiver_method_return()
    {
        // `auto p = source->GetPipeline(); p->Play();` — chained via receiver method return type.
        var header = """
            namespace demo {
                public ref class Pipeline { public: void Play(); };
                public ref class Source { public: Pipeline^ GetPipeline(); };
                public ref class Mgr { public: void Run(Source^ source); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Source^ source)
                {
                    auto p = source->GetPipeline();
                    p->Play();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "p->Play") + "p->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Pipeline", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Does_not_confuse_equality_check_with_declaration()
    {
        // `if (strategy == nullptr)` must not be mistaken for a `strategy =` declaration.
        // Real-world C++/CLI pattern: the declaration line is far above; the equality check sits
        // between the declaration and the eventual method call.
        var header = """
            namespace demo {
                public ref class Src {};
                public ref class Dst {};
                public interface class IConnectionStrategy {
                public:
                    virtual void ConfigurePipeline(Src^ s, Dst^ d) = 0;
                };
                public ref class Mgr {
                public:
                    void Run(Src^ source, Dst^ destination);
                private:
                    IConnectionStrategy^ ResolveStrategy(Src^ source, Dst^ destination);
                };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Src^ source, Dst^ destination)
                {
                    auto strategy = ResolveStrategy(source, destination);
                    if (strategy == nullptr)
                    {
                        return;
                    }
                    strategy->ConfigurePipeline(source, destination);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "strategy->ConfigurePipeline") + "strategy->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.IConnectionStrategy", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Does_not_confuse_re_assignment_with_declaration()
    {
        // `strategy = otherThing;` (re-assignment) must not shadow the earlier
        // `auto strategy = ResolveStrategy(...);` declaration.
        var header = """
            namespace demo {
                public ref class Src {};
                public interface class IConnectionStrategy { public: void Ping(); };
                public ref class Mgr {
                public:
                    void Run(Src^ source);
                    IConnectionStrategy^ Alternate();
                private:
                    IConnectionStrategy^ ResolveStrategy(Src^ source);
                };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Src^ source)
                {
                    auto strategy = ResolveStrategy(source);
                    strategy = Alternate();
                    strategy->Ping();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "strategy->Ping") + "strategy->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.IConnectionStrategy", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_on_auto_from_receiver_property_access()
    {
        // Real-world C++/CLI pattern in Disconnect(): `auto strategy = handle->Strategy;`
        // followed by `strategy->StopAudioFlow(...)`. Initializer is a property
        // access (no parens), not a method call.
        var header = """
            namespace demo {
                public ref class Src {};
                public ref class Dst {};
                public ref class Pipeline {};
                public interface class IConnectionStrategy {
                public:
                    virtual void StopAudioFlow(Pipeline^ p, Src^ s, Dst^ d, bool isHost) = 0;
                };
                public ref class Handle {
                public:
                    property IConnectionStrategy^ Strategy;
                };
                public ref class Mgr { public: void Run(Handle^ handle); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Handle^ handle)
                {
                    auto strategy = handle->Strategy;
                    strategy->StopAudioFlow(nullptr, nullptr, nullptr, true);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "strategy->StopAudioFlow") + "strategy->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.IConnectionStrategy", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_on_auto_from_host_field_reference()
    {
        // Initializer is a bare identifier referring to a host-type field/property.
        var header = """
            namespace demo {
                public ref class Widget { public: void Kill(); };
                public ref class Host {
                    Widget^ m_widget;
                public:
                    void Run();
                };
            }
            """;
        var impl = """
            namespace demo {
                void Host::Run()
                {
                    auto w = m_widget;
                    w->Kill();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Host.h", header, "Host.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Host")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "w->Kill") + "w->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Host"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_chained_property_receiver_a_arrow_b_arrow_method()
    {
        // Real-world C++/CLI pattern: `destination->Parent->DisconnectSource(connectionId);`
        // Parent is a property on the destination's type; DisconnectSource is a
        // method on Parent's type.
        var header = """
            namespace demo {
                public interface class IAudioDestination {
                public:
                    virtual void DisconnectSource(int id) = 0;
                    property IAudioDestination^ Parent
                    {
                        IAudioDestination^ get();
                        void set(IAudioDestination^ value);
                    }
                };
                public ref class Mgr { public: void Run(IAudioDestination^ destination); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(IAudioDestination^ destination)
                {
                    destination->Parent->DisconnectSource(42);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "Parent->DisconnectSource") + "Parent->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.IAudioDestination", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Contains("DisconnectSource", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_three_level_chain()
    {
        var header = """
            namespace demo {
                public ref class C { public: void Bang(); };
                public ref class B { public: property C^ Cee; };
                public ref class A { public: property B^ Bee; };
                public ref class Mgr { public: void Run(A^ a); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(A^ a)
                {
                    a->Bee->Cee->Bang();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "Cee->Bang") + "Cee->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.C", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Resolves_method_call_on_auto_from_dynamic_cast()
    {
        // Real-world C++/CLI pattern: `auto fileSource = dynamic_cast<FileAudioSource^>(source);`
        // followed by `fileSource->Disconnect(connectionId);`.
        var header = """
            namespace demo {
                public ref class FileAudioSource {
                public:
                    int Disconnect(int id);
                };
                public ref class Source {};
                public ref class Mgr { public: void Run(Source^ source); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Source^ source)
                {
                    auto fileSource = dynamic_cast<FileAudioSource^>(source);
                    int remaining = fileSource->Disconnect(42);
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "fileSource->Disconnect") + "fileSource->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.FileAudioSource", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Contains("Disconnect", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_static_cast_and_safe_cast_initializers()
    {
        var header = """
            namespace demo {
                public ref class Widget { public: void Ping(); };
                public ref class Src {};
                public ref class Mgr { public: void Run(Src^ source); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Src^ source)
                {
                    auto a = static_cast<Widget^>(source);
                    a->Ping();
                    auto b = safe_cast<Widget^>(source);
                    b->Ping();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offA = OffsetOf(implText, "a->Ping") + "a->".Length + 1;
        var resA = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offA, TypeSiteSignature, out _);
        Assert.NotNull(resA);
        Assert.Equal("demo.Widget", resA!.Value.OwnerType.FullyQualifiedName);

        var offB = OffsetOf(implText, "b->Ping") + "b->".Length + 1;
        var resB = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offB, TypeSiteSignature, out _);
        Assert.NotNull(resB);
        Assert.Equal("demo.Widget", resB!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Does_not_confuse_assignment_rhs_reference_with_declaration()
    {
        // Real-world C++/CLI: `handle->Pipeline = pipeline;` then `handle->IsHost = pipeline->IsHost;`
        // Clicking on the RHS IsHost — the RHS receiver `pipeline` appears earlier
        // inside `handle->Pipeline = pipeline;` on the RHS. That earlier occurrence
        // must not be captured as a *declaration* of `pipeline`.
        var header = """
            namespace demo {
                public ref class AudioPipeline { public: property bool IsHost; };
                public ref class Handle { public: property AudioPipeline^ Pipeline; property bool IsHost; };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    auto pipeline = gcnew AudioPipeline();
                    auto handle = gcnew Handle();
                    handle->Pipeline = pipeline;
                    handle->IsHost = pipeline->IsHost;
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        // Click on RHS `IsHost` (second occurrence of "IsHost" as `pipeline->IsHost`).
        var firstIdx = implText.IndexOf("pipeline->IsHost", StringComparison.Ordinal);
        var offset = firstIdx + "pipeline->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.AudioPipeline", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Equal("IsHost", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_method_call_on_auto_from_indexer_of_generic_local()
    {
        // Real-world C++/CLI pattern: `List<IAudioDestination^>^ children;` then
        // `auto firstChild = children[0];` — indexer yields element type IAudioDestination.
        // Then `firstChild->Parent = nullptr;` must resolve Parent on IAudioDestination.
        var header = """
            namespace demo {
                public interface class IAudioDestination {
                public:
                    property IAudioDestination^ Parent;
                };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    List<IAudioDestination^>^ children;
                    auto firstChild = children[0];
                    firstChild->Parent = nullptr;
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "firstChild->Parent") + "firstChild->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.IAudioDestination", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Equal("Parent", result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_indexer_on_generic_parameter()
    {
        var header = """
            namespace demo {
                public ref class Item { public: void Do(); };
                public ref class Mgr { public: void Run(List<Item^>^ items); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(List<Item^>^ items)
                {
                    auto first = items[0];
                    first->Do();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "first->Do") + "first->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Item", result!.Value.OwnerType.FullyQualifiedName);
    }

    [Fact]
    public void Member_access_prefers_property_over_same_named_type()
    {
        // Real-world C++/CLI pattern: a class `EqualizerProcessor` AND a property
        // `Handle::EqualizerProcessor` share the same identifier. Clicking on the
        // property in `handle->EqualizerProcessor = ...;` must jump to the property,
        // not to the class declaration.
        var header = """
            namespace demo {
                public ref class EqualizerProcessor { public: void Do(); };
                public ref class Handle {
                public:
                    property EqualizerProcessor^ EqualizerProcessor;
                };
                public ref class Mgr { public: void Run(Handle^ handle); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run(Handle^ handle)
                {
                    handle->EqualizerProcessor = nullptr;
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "handle->EqualizerProcessor") + "handle->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        // Must resolve to Handle's property, NOT the EqualizerProcessor class.
        Assert.Equal("demo.Handle", result!.Value.OwnerType.FullyQualifiedName);
        Assert.NotEqual(TypeSiteSignature, result.Value.Member.Signature);
    }

    [Fact]
    public void Resolves_file_level_free_function_call()
    {
        // Real-world C++/CLI pattern: `static IAudioProcessor^ EnrollProcessor(...) { ... }`
        // defined at namespace top-level, called from a member impl in the same file.
        var header = """
            namespace demo {
                public ref class Pipeline { public: void AddProcessor(int p); };
                public ref class Mgr { public: void Run(Pipeline^ pipeline); };
            }
            """;
        var impl = """
            namespace demo {
                static int EnrollProcessor(Pipeline^ pipeline, int proc)
                {
                    pipeline->AddProcessor(proc);
                    return proc;
                }

                void Mgr::Run(Pipeline^ pipeline)
                {
                    int r = EnrollProcessor(pipeline, 42);
                }
            }
            """;
        var (cpp, implText, implPath) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        // Click on the call to EnrollProcessor.
        var offset = implText.IndexOf("EnrollProcessor(pipeline, 42)", StringComparison.Ordinal) + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.StartsWith(CppContextClickResolver.FileFunctionOwnerPrefix, result!.Value.OwnerType.FullyQualifiedName);
        Assert.Equal($"{CppContextClickResolver.FileFunctionSignaturePrefix}EnrollProcessor",
                     result.Value.Member.Signature);

        // The FileFunctionsByFilePath index should carry the function.
        Assert.True(cpp.FileFunctionsByFilePath.ContainsKey(implPath));
        var fns = cpp.FileFunctionsByFilePath[implPath];
        Assert.Contains(fns, f => f.Name == "EnrollProcessor");
    }

    [Fact]
    public void Member_access_does_not_leak_to_same_named_type_when_member_missing()
    {
        // If `handle->EqualizerProcessor` resolves as a member successfully, we return
        // the property. If member resolution fails (e.g. receiver type unknown), we must
        // NOT redirect to a same-named class — that would misdirect the jump.
        var header = """
            namespace demo {
                public ref class EqualizerProcessor { public: void Do(); };
                public ref class Mgr { public: void Run(); };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    // `unknownHandle` has no known type — receiver inference must fail.
                    unknownHandle->EqualizerProcessor = nullptr;
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        var offset = OffsetOf(implText, "unknownHandle->EqualizerProcessor") + "unknownHandle->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out var reason);

        Assert.Null(result); // must not jump to the EqualizerProcessor class
        Assert.Contains("receiver", reason);
    }

    [Fact]
    public void Uses_actual_enclosing_method_when_click_is_in_sibling_body()
    {
        // The viewer was opened on Run (contextMember), but the user scrolled to
        // Attach() and clicked inside its body. Even though contextMember says "Run",
        // the resolver must locate the enclosing Attach() method to pick up the
        // right parameter list (`handle` is Attach's param, not Run's).
        var header = """
            namespace demo {
                public ref class Widget { public: void Do(); };
                public ref class Mgr {
                public:
                    void Run();
                    void Attach(Widget^ handle);
                };
            }
            """;
        var impl = """
            namespace demo {
                void Mgr::Run()
                {
                    int x = 42;
                }

                void Mgr::Attach(Widget^ handle)
                {
                    handle->Do();
                }
            }
            """;
        var (cpp, implText, _) = BuildImplPair("Mgr.h", header, "Mgr.cpp", impl);
        var run = cpp.GetTypeByFullyQualifiedName("demo.Mgr")!.Members.Single(m => m.Name == "Run");

        // Click on Do inside Attach body, but pass Run's MemberRef as contextMember.
        var offset = OffsetOf(implText, "handle->Do") + "handle->".Length + 1;

        var result = CppContextClickResolver.TryResolve(
            cpp, new TypeRef("demo.Mgr"), run.Ref(), offset, TypeSiteSignature, out _);

        Assert.NotNull(result);
        Assert.Equal("demo.Widget", result!.Value.OwnerType.FullyQualifiedName);
        Assert.Contains("Do", result.Value.Member.Signature);
    }

    [Fact]
    public void Returns_null_when_host_type_is_unknown_to_cpp()
    {
        var cpp = CppCompilation.Create(Array.Empty<CppSyntaxTree>());
        var result = CppContextClickResolver.TryResolve(
            cpp,
            new TypeRef("some.Missing"),
            new MemberRef(new TypeRef("some.Missing"), "void Whatever()"),
            offsetInSource: 0,
            TypeSiteSignature, out _);
        Assert.Null(result);
    }
}

internal static class CppMemberSymbolTestExtensions
{
    // Small helper: build a MemberRef from a CppMemberSymbol without going through
    // Kata.Core.Model plumbing — matches the shape the adapter would produce.
    public static MemberRef Ref(this CppMemberSymbol m)
        => new(new TypeRef(m.ContainingType.FullyQualifiedName), m.Signature);
}
