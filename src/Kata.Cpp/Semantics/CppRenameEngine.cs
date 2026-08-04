using Kata.Core.Diff;
using Kata.Cpp.Syntax;

namespace Kata.Cpp.Semantics;

/// <summary>
/// Semantic Cpp rename backed by <see cref="CppReferenceFinder"/>. Unlike the
/// legacy regex-based rename in <c>CppCliRefactorEngine</c>, this rename only
/// touches token positions the reference finder has already vetted (name +
/// syntactic context + arity), so unrelated same-name identifiers are left
/// alone.
/// </summary>
public static class CppRenameEngine
{
    public static IReadOnlyList<DocumentChange> RenameType(
        CppCompilation compilation,
        CppTypeSymbol type,
        string newName)
    {
        if (string.IsNullOrEmpty(newName)) return Array.Empty<DocumentChange>();
        var refs = CppReferenceFinder.FindTypeReferences(compilation, type);
        return BuildChanges(refs, newName);
    }

    public static IReadOnlyList<DocumentChange> RenameMember(
        CppCompilation compilation,
        CppMemberSymbol member,
        string newName)
    {
        if (string.IsNullOrEmpty(newName)) return Array.Empty<DocumentChange>();
        var refs = CppReferenceFinder.FindMemberReferences(compilation, member);
        return BuildChanges(refs, newName);
    }

    /// <summary>
    /// Rename a parameter of the given method. Rewrites: the parameter name in
    /// the header declaration's parameter list, the same in the .cpp implementation's
    /// parameter list, plus every bare identifier occurrence with the old name inside
    /// the method body (best-effort — a local var shadowing the parameter would be
    /// swept too, but that's rare in practice).
    /// </summary>
    public static IReadOnlyList<DocumentChange> RenameParameter(
        CppCompilation compilation,
        CppMemberSymbol member,
        string oldParamName,
        string newParamName)
    {
        if (string.IsNullOrEmpty(oldParamName) || string.IsNullOrEmpty(newParamName)) return Array.Empty<DocumentChange>();
        if (string.Equals(oldParamName, newParamName, StringComparison.Ordinal)) return Array.Empty<DocumentChange>();

        var positionsByFile = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        void CollectFrom(CppDeclarationSite site, bool isImplementation)
        {
            var tree = FindTree(compilation, site.FilePath);
            if (tree is null) return;
            var tokens = tree.Tokens;

            int methodIdx = FindTokenIndexByPosition(tokens, site.Span.Start);
            if (methodIdx < 0) return;

            // Walk forward from methodIdx to find the '(' that opens the parameter list.
            int openParenIdx = -1;
            for (int i = methodIdx; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == CppTokenKind.Punctuation && tokens[i].Text == "(")
                {
                    openParenIdx = i;
                    break;
                }
            }
            if (openParenIdx < 0) return;

            // Walk to matching ')'; collect parameter-list occurrences of oldParamName at depth == 1.
            int closeParenIdx = -1;
            int depthParen = 0;
            for (int i = openParenIdx; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == CppTokenKind.Punctuation)
                {
                    if (t.Text == "(") depthParen++;
                    else if (t.Text == ")")
                    {
                        depthParen--;
                        if (depthParen == 0) { closeParenIdx = i; break; }
                    }
                }
                if (depthParen == 1 && t.Kind == CppTokenKind.Identifier && t.Text == oldParamName)
                {
                    AddPos(positionsByFile, site.FilePath, t.Position);
                }
            }
            if (closeParenIdx < 0) return;

            // For the implementation site, sweep the method body too.
            if (!isImplementation) return;

            int braceOpenIdx = -1;
            for (int i = closeParenIdx + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != CppTokenKind.Punctuation) continue;
                if (tokens[i].Text == "{") { braceOpenIdx = i; break; }
                if (tokens[i].Text == ";") return; // declaration-only (rare for impl, but safe)
            }
            if (braceOpenIdx < 0) return;

            int depthBrace = 0;
            for (int i = braceOpenIdx; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == CppTokenKind.Punctuation)
                {
                    if (t.Text == "{") depthBrace++;
                    else if (t.Text == "}")
                    {
                        depthBrace--;
                        if (depthBrace == 0) break;
                    }
                }
                if (depthBrace >= 1 && t.Kind == CppTokenKind.Identifier && t.Text == oldParamName)
                {
                    AddPos(positionsByFile, site.FilePath, t.Position);
                }
            }
        }

        CollectFrom(member.DeclarationSite, isImplementation: false);
        if (member.ImplementationSite is { } impl) CollectFrom(impl, isImplementation: true);

        // Emit DocumentChange per file. Positions descending so earlier edits don't shift.
        var changes = new List<DocumentChange>(positionsByFile.Count);
        foreach (var (filePath, positions) in positionsByFile)
        {
            string original;
            try { original = File.ReadAllText(filePath); }
            catch { continue; }

            var unique = positions.Distinct().OrderByDescending(x => x).ToList();
            var sb = new System.Text.StringBuilder(original);
            var oldLen = oldParamName.Length;
            foreach (var p in unique)
            {
                if (p < 0 || p + oldLen > original.Length) continue;
                if (original.Substring(p, oldLen) != oldParamName) continue; // safety
                sb.Remove(p, oldLen);
                sb.Insert(p, newParamName);
            }
            var updated = sb.ToString();
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, original, updated));
            }
        }
        return changes;
    }

    private static void AddPos(Dictionary<string, List<int>> byFile, string filePath, int pos)
    {
        if (!byFile.TryGetValue(filePath, out var list))
        {
            list = new List<int>();
            byFile[filePath] = list;
        }
        list.Add(pos);
    }

    private static CppSyntaxTree? FindTree(CppCompilation compilation, string filePath)
    {
        foreach (var t in compilation.SyntaxTrees)
        {
            if (string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) return t;
        }
        foreach (var t in compilation.ImplementationTrees)
        {
            if (string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return null;
    }

    private static int FindTokenIndexByPosition(IReadOnlyList<CppToken> tokens, int position)
    {
        // Binary-searchable since tokens are position-sorted.
        int lo = 0, hi = tokens.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (tokens[mid].Position == position) return mid;
            if (tokens[mid].Position < position) lo = mid + 1;
            else hi = mid - 1;
        }
        // Fallback: linear scan (positions may not exactly match if the token was skipped).
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Position >= position) return i;
        }
        return -1;
    }

    private static IReadOnlyList<DocumentChange> BuildChanges(IReadOnlyList<CppReference> references, string newName)
    {
        if (references.Count == 0) return Array.Empty<DocumentChange>();

        var byFile = references
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changes = new List<DocumentChange>(byFile.Count);
        foreach (var group in byFile)
        {
            var filePath = group.Key;
            string original;
            try { original = File.ReadAllText(filePath); }
            catch { continue; }

            var updated = ApplyReplacements(original, group, newName);
            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                changes.Add(new DocumentChange(filePath, DocumentChangeKind.Modified, OldText: original, NewText: updated));
            }
        }
        return changes;
    }

    private static string ApplyReplacements(string original, IEnumerable<CppReference> references, string newName)
    {
        // Sort by SpanStart descending so earlier edits don't shift later positions.
        var ordered = references
            .Where(r => r.SpanStart >= 0 && r.SpanStart + r.SpanLength <= original.Length)
            .OrderByDescending(r => r.SpanStart)
            .ToList();
        if (ordered.Count == 0) return original;

        var sb = new System.Text.StringBuilder(original);
        foreach (var r in ordered)
        {
            sb.Remove(r.SpanStart, r.SpanLength);
            sb.Insert(r.SpanStart, newName);
        }
        return sb.ToString();
    }
}
