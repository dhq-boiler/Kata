using System.Text;
using Kata.Core.Intents;

namespace Kata.Core.Diff;

public static class InstructionExporter
{
    public static string ExportMarkdown(
        RefactoringIntent? intent,
        ChangeSet changeSet,
        string? title = null,
        string? generatedAt = null)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(title ?? DefaultTitle(intent, changeSet));
        sb.AppendLine();
        if (!string.IsNullOrEmpty(generatedAt))
        {
            sb.Append("_Generated: ").Append(generatedAt).AppendLine("_");
            sb.AppendLine();
        }

        sb.AppendLine("## What");
        sb.AppendLine();
        sb.Append("- ").AppendLine(changeSet.Summary);
        if (intent is not null)
        {
            sb.Append("- Intent kind: `").Append(intent.GetType().Name).AppendLine("`");
            var affected = IntentAffectedTypes.Extract(intent);
            if (affected.Count > 0)
            {
                sb.Append("- Affected types: ");
                sb.AppendLine(string.Join(", ", affected.Select(t => "`" + t.FullyQualifiedName + "`")));
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Why");
        sb.AppendLine();
        var rationale = intent?.Rationale;
        sb.AppendLine(string.IsNullOrWhiteSpace(rationale) ? "_(no rationale supplied)_" : rationale);
        sb.AppendLine();

        if (intent is not null)
        {
            var call = McpCallDescriptor.Describe(intent);
            if (call is not null)
            {
                sb.AppendLine("## How to reproduce (MCP tool call)");
                sb.AppendLine();
                sb.AppendLine("Team members can run this via the `kata` MCP server:");
                sb.AppendLine();
                sb.AppendLine("```yaml");
                sb.Append("tool: ").AppendLine(call.ToolName);
                sb.AppendLine("arguments:");
                foreach (var (key, value) in call.Arguments)
                {
                    if (value is null) continue;
                    sb.Append("  ").Append(key).Append(": ");
                    sb.AppendLine(RenderYamlValue(value));
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.Append("## File changes (");
        sb.Append(changeSet.Changes.Count);
        sb.AppendLine(")");
        sb.AppendLine();

        var index = 1;
        foreach (var change in changeSet.Changes)
        {
            sb.Append("### ").Append(index++).Append(". ").Append(change.Kind).Append(" — `").Append(change.FilePath).AppendLine("`");
            sb.AppendLine();

            switch (change.Kind)
            {
                case DocumentChangeKind.Added:
                    sb.AppendLine("**New file:**");
                    sb.AppendLine();
                    AppendCodeFence(sb, change.FilePath, change.NewText ?? string.Empty);
                    break;

                case DocumentChangeKind.Deleted:
                    sb.AppendLine("**Deleted file (was):**");
                    sb.AppendLine();
                    AppendCodeFence(sb, change.FilePath, change.OldText ?? string.Empty);
                    break;

                case DocumentChangeKind.Modified:
                default:
                    sb.AppendLine("**Before:**");
                    sb.AppendLine();
                    AppendCodeFence(sb, change.FilePath, change.OldText ?? string.Empty);
                    sb.AppendLine();
                    sb.AppendLine("**After:**");
                    sb.AppendLine();
                    AppendCodeFence(sb, change.FilePath, change.NewText ?? string.Empty);
                    break;
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string DefaultTitle(RefactoringIntent? intent, ChangeSet changeSet)
    {
        if (intent is null) return $"Refactoring instruction ({changeSet.Changes.Count} file changes)";
        return $"Refactoring instruction: {intent.GetType().Name}";
    }

    private static void AppendCodeFence(StringBuilder sb, string filePath, string text)
    {
        var lang = InferLanguage(filePath);
        sb.Append("```").AppendLine(lang);
        sb.Append(text);
        if (!text.EndsWith("\n", StringComparison.Ordinal)) sb.AppendLine();
        sb.AppendLine("```");
    }

    private static string InferLanguage(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".h" or ".hpp" or ".hh" or ".hxx" => "cpp",
            ".cpp" or ".cc" or ".cxx" => "cpp",
            ".xml" or ".vcxproj" or ".csproj" or ".slnx" => "xml",
            _ => string.Empty,
        };
    }

    private static string RenderYamlValue(object value)
    {
        switch (value)
        {
            case string s:
                return NeedsQuotes(s) ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : s;
            case bool b:
                return b ? "true" : "false";
            case System.Collections.IEnumerable enumerable when value is not string:
            {
                var items = new List<string>();
                foreach (var item in enumerable) items.Add(item?.ToString() ?? string.Empty);
                if (items.Count == 0) return "[]";
                var sb = new StringBuilder();
                sb.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(NeedsQuotes(items[i]) ? "\"" + items[i].Replace("\"", "\\\"") + "\"" : items[i]);
                }
                sb.Append(']');
                return sb.ToString();
            }
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static bool NeedsQuotes(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c is '_' or '.' or '-' or '/') continue;
            return true;
        }
        return false;
    }
}
