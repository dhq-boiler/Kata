using Kata.Core.Sln;
using Kata.Cpp.Semantics;
using Kata.Cpp.Syntax;

namespace Kata.Tests;

/// <summary>
/// Diagnostic tests against a real C++/CLI codebase on disk. Skipped when the
/// path isn't present so CI stays green on machines without the checkout.
/// </summary>
public sealed class CppParserDiagnosticTests
{
    private const string NativeLibRoot = @"C:\Git\_kata_diagnostic_disabled_\nativeLib\Interfaces";

    private static bool NativeLibAvailable() => Directory.Exists(NativeLibRoot);

    [Fact]
    public void Parses_real_ISource_header()
    {
        if (!NativeLibAvailable())
        {
            return; // silent skip on machines without the checkout
        }

        var path = Path.Combine(NativeLibRoot, "ISource.h");
        Assert.True(File.Exists(path), $"header missing: {path}");

        var text = File.ReadAllText(path);
        var comp = CppCompilation.Create(new[] { CppSyntaxTree.Parse(path, text) });

        var t = comp.GetTypeByFullyQualifiedName("nativeLib.ISource");
        Assert.NotNull(t);
        Assert.Contains(t!.Members, m => m.Name == "Parent");
    }

    [Fact]
    public void Loads_vcxproj_and_registers_ISource()
    {
        const string vcxproj = @"C:\Git\_kata_diagnostic_disabled_\nativeLib\nativeLib.vcxproj";
        if (!File.Exists(vcxproj))
        {
            return;
        }

        var comp = CppCompilation.FromVcxProj(vcxproj);
        var registered = comp.AllTypes.Select(t => t.FullyQualifiedName).OrderBy(x => x).ToList();

        var iface = comp.GetTypeByFullyQualifiedName("nativeLib.ISource");
        if (iface is null)
        {
            var similarNames = registered
                .Where(n => n.Contains("ISource", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("Audio", StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToArray();
            Assert.Fail(
                $"ISource missing. Registered similar: {string.Join(", ", similarNames)}. "
                + $"Total registered types: {registered.Count}");
        }
    }

    [Fact]
    public void Parses_ConnectionManager_members_including_ResolveStrategy()
    {
        const string vcxproj = @"C:\Git\_kata_diagnostic_disabled_\nativeLib\nativeLib.vcxproj";
        if (!File.Exists(vcxproj)) return;

        var comp = CppCompilation.FromVcxProj(vcxproj);
        var mgr = comp.GetTypeByFullyQualifiedName("nativeLib.ConnectionManager");
        Assert.NotNull(mgr);

        var members = mgr!.Members.Select(m => $"{m.Kind}/{m.Name}::{m.ReturnTypeDisplay}").ToArray();
        var resolveStrategy = mgr.Members.FirstOrDefault(m => m.Name == "ResolveStrategy");
        if (resolveStrategy is null)
        {
            Assert.Fail($"ResolveStrategy missing. Members: {string.Join(" | ", members.Take(30))}");
        }

        // Verify return-type text is what NormalizeCppTypeName can canonicalise to a known Cpp type.
        var normalized = Kata.Core.Model.SymbolKeyFormatter.NormalizeCppTypeName(resolveStrategy!.ReturnTypeDisplay);
        var iface = comp.GetTypeByFullyQualifiedName($"nativeLib.{normalized}");
        Assert.NotNull(iface);
    }

    [Fact]
    public void Parses_AudioPipeline_IsHost_property()
    {
        const string vcxproj = @"C:\Git\_kata_diagnostic_disabled_\nativeLib\nativeLib.vcxproj";
        if (!File.Exists(vcxproj)) return;

        var comp = CppCompilation.FromVcxProj(vcxproj);
        var pipe = comp.GetTypeByFullyQualifiedName("nativeLib.AudioPipeline");
        Assert.NotNull(pipe);

        var propCount = pipe!.Members.Count(m => m.Kind == Kata.Core.Model.MemberKind.Property);
        var isHost = pipe.Members.FirstOrDefault(m => m.Name == "IsHost");
        if (isHost is null)
        {
            var all = pipe.Members.Select(m => $"{m.Kind}/{m.Name}");
            Assert.Fail($"IsHost missing on AudioPipeline. Property count={propCount}. All members: {string.Join(" | ", all)}");
        }
    }

    [Fact]
    public void Parses_ConnectionHandle_EqualizerProcessor_property()
    {
        const string vcxproj = @"C:\Git\_kata_diagnostic_disabled_\nativeLib\nativeLib.vcxproj";
        if (!File.Exists(vcxproj)) return;

        var comp = CppCompilation.FromVcxProj(vcxproj);
        var t = comp.GetTypeByFullyQualifiedName("nativeLib.ConnectionHandle");
        Assert.NotNull(t);

        var eq = t!.Members.FirstOrDefault(m => m.Name == "EqualizerProcessor");
        if (eq is null)
        {
            var props = t.Members.Where(m => m.Kind == Kata.Core.Model.MemberKind.Property).Select(m => m.Name);
            Assert.Fail($"EqualizerProcessor missing. Properties: [{string.Join(", ", props)}]. Total members: {t.Members.Count}");
        }
        Assert.Equal(Kata.Core.Model.MemberKind.Property, eq!.Kind);
    }

    [Fact]
    public void Parses_AttachProcessors_parameters()
    {
        const string vcxproj = @"C:\Git\_kata_diagnostic_disabled_\nativeLib\nativeLib.vcxproj";
        if (!File.Exists(vcxproj)) return;

        var comp = CppCompilation.FromVcxProj(vcxproj);
        var mgr = comp.GetTypeByFullyQualifiedName("nativeLib.ConnectionManager");
        Assert.NotNull(mgr);

        var attach = mgr!.Members.FirstOrDefault(m => m.Name == "AttachProcessors");
        if (attach is null)
        {
            var samples = mgr.Members.Where(m => m.Kind == Kata.Core.Model.MemberKind.Method).Select(m => m.Name).Take(20);
            Assert.Fail($"AttachProcessors missing. First methods: [{string.Join(", ", samples)}]. Total: {mgr.Members.Count}");
        }

        var paramsInfo = string.Join(" | ", attach!.Parameters.Select(p => $"{p.Type}::{p.Name}"));
        if (attach.Parameters.Count != 2)
        {
            Assert.Fail($"Expected 2 params, got {attach.Parameters.Count}. Detail: {paramsInfo}");
        }

        // Check the handle parameter specifically.
        var handleParam = attach.Parameters.FirstOrDefault(p => p.Name == "handle");
        if (handleParam is null)
        {
            Assert.Fail($"handle param missing. All params: {paramsInfo}");
        }

        var normalized = Kata.Core.Model.SymbolKeyFormatter.NormalizeCppTypeName(handleParam!.Type);
        Assert.Equal("ConnectionHandle", normalized);
    }

    [Fact]
    public void Sln_discovery_matches_Kata_App_flow()
    {
        // Path is intentionally set to a directory that does not exist so this
        // diagnostic silently skips everywhere. The other tests in this file
        // guard on the nativeLib.vcxproj which also does not exist, so they
        // skip too. Point this constant at a real .sln locally to re-enable.
        const string sln = @"C:\Git\_kata_diagnostic_disabled_\NativeLibHost.sln";
        if (!File.Exists(sln))
        {
            return;
        }

        var discovered = SolutionProjectDiscovery.DiscoverForeignProjects(
            sln,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vcxproj" });
        var vcx = discovered
            .Where(d => d.Extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var headerTrees = new List<CppSyntaxTree>();
        var implTrees = new List<CppSyntaxTree>();
        foreach (var d in vcx)
        {
            headerTrees.AddRange(CppCliProjectLoader.LoadSyntaxTrees(d.AbsolutePath));
            implTrees.AddRange(CppCliProjectLoader.LoadImplementationTrees(d.AbsolutePath));
        }
        var comp = CppCompilation.Create(headerTrees, implTrees);

        var registered = comp.AllTypes.Select(t => t.FullyQualifiedName).OrderBy(x => x).ToList();
        var iface = comp.GetTypeByFullyQualifiedName("nativeLib.ISource");
        if (iface is null)
        {
            var similar = registered
                .Where(n => n.Contains("ISource", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToArray();
            var vcxNames = string.Join(", ", vcx.Select(d => d.Name));
            Assert.Fail(
                $"ISource missing after sln flow. vcxproj discovered: [{vcxNames}]. "
                + $"Similar names: [{string.Join(", ", similar)}]. Total types: {registered.Count}.");
        }
    }
}
