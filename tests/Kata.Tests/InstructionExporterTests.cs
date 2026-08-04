using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.Tests;

public sealed class InstructionExporterTests
{
    private static readonly TypeRef Foo = new("Ns.Foo");
    private static readonly TypeRef Bar = new("Ns.Bar");

    [Fact]
    public void McpCallDescriptor_maps_pull_up_method_to_expected_tool_call()
    {
        var intent = IntentFactory.PullUpMethod(Foo, Bar, new[] { new MemberRef(Foo, "M()") }, IntentSource.Human, "why");
        var call = McpCallDescriptor.Describe(intent);
        Assert.NotNull(call);
        Assert.Equal("propose_pull_up_method", call!.ToolName);
        var argMap = call.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("Ns.Foo", argMap["subclassFullName"]);
        Assert.Equal("Ns.Bar", argMap["parentFullName"]);
        Assert.Equal("why", argMap["rationale"]);
    }

    [Fact]
    public void McpCallDescriptor_maps_rename_field_correctly()
    {
        var intent = IntentFactory.RenameField(Foo, new MemberRef(Foo, "totalAmount"), "TotalAmount", IntentSource.Ai);
        var call = McpCallDescriptor.Describe(intent);
        Assert.NotNull(call);
        Assert.Equal("propose_rename_field", call!.ToolName);
        var argMap = call.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("Ns.Foo", argMap["ownerFullName"]);
        Assert.Equal("totalAmount", argMap["fieldSignature"]);
        Assert.Equal("TotalAmount", argMap["newName"]);
    }

    [Fact]
    public void ExportMarkdown_includes_summary_rationale_and_mcp_call_yaml()
    {
        var intent = IntentFactory.CollapseHierarchy(Foo, Bar, IntentSource.Human, "consolidate leaf hierarchy");
        var changeSet = new ChangeSet(
            AppliedIntentIds: new List<System.Guid> { intent.Id },
            Changes: new List<DocumentChange>
            {
                new("C:/proj/Bar.cs", DocumentChangeKind.Modified, OldText: "public class Bar {}", NewText: "public class Bar { void X(){} }"),
                new("C:/proj/Foo.cs", DocumentChangeKind.Deleted, OldText: "public class Foo : Bar {}", NewText: null),
            },
            Summary: "Collapse Foo into Bar");

        var md = InstructionExporter.ExportMarkdown(intent, changeSet, generatedAt: "2026-08-01T10:00:00");

        Assert.Contains("# Refactoring instruction: CollapseHierarchyIntent", md);
        Assert.Contains("_Generated: 2026-08-01T10:00:00_", md);
        Assert.Contains("consolidate leaf hierarchy", md);
        Assert.Contains("Collapse Foo into Bar", md);
        Assert.Contains("Affected types: `Ns.Foo`, `Ns.Bar`", md);
        Assert.Contains("## How to reproduce (MCP tool call)", md);
        Assert.Contains("tool: propose_collapse_hierarchy", md);
        Assert.Contains("subclassFullName: Ns.Foo", md);
        Assert.Contains("parentFullName: Ns.Bar", md);
        Assert.Contains("### 1. Modified — `C:/proj/Bar.cs`", md);
        Assert.Contains("### 2. Deleted — `C:/proj/Foo.cs`", md);
        Assert.Contains("**Before:**", md);
        Assert.Contains("**After:**", md);
        Assert.Contains("**Deleted file (was):**", md);
        Assert.Contains("```csharp", md);
    }

    [Fact]
    public void ExportMarkdown_renders_added_files_as_new_file_block()
    {
        var intent = IntentFactory.AddGhostType("NewType", new NamespaceRef("Ns"), TypeKind.Class, IntentSource.Human);
        var changeSet = new ChangeSet(
            AppliedIntentIds: new List<System.Guid> { intent.Id },
            Changes: new List<DocumentChange>
            {
                new("C:/proj/NewType.cs", DocumentChangeKind.Added, OldText: null, NewText: "namespace Ns; public class NewType {}"),
            },
            Summary: "Add NewType");

        var md = InstructionExporter.ExportMarkdown(intent, changeSet);
        Assert.Contains("**New file:**", md);
        Assert.Contains("public class NewType", md);
        Assert.Contains("tool: propose_add_ghost_type", md);
        Assert.Contains("kind: Class", md);
    }

    [Fact]
    public void ExportMarkdown_works_without_intent()
    {
        var changeSet = new ChangeSet(
            AppliedIntentIds: new List<System.Guid>(),
            Changes: new List<DocumentChange>
            {
                new("C:/proj/File.cs", DocumentChangeKind.Modified, OldText: "old", NewText: "new"),
            },
            Summary: "Manual change");

        var md = InstructionExporter.ExportMarkdown(intent: null, changeSet);
        Assert.Contains("# Refactoring instruction (1 file changes)", md);
        Assert.Contains("Manual change", md);
        // No intent → no MCP tool block.
        Assert.DoesNotContain("## How to reproduce", md);
        Assert.Contains("_(no rationale supplied)_", md);
    }
}
