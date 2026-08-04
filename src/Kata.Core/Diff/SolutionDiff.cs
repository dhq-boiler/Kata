using Kata.Core.Model;

namespace Kata.Core.Diff;

public enum DiffState
{
    Unchanged,
    Added,
    Removed,
    Modified,
}

public sealed record MemberDiffEntry(
    MemberRef Ref,
    string Name,
    DiffState State);

public sealed record TypeDiffEntry(
    TypeRef Ref,
    string Name,
    NamespaceRef Namespace,
    DiffState State,
    IReadOnlyList<MemberDiffEntry> MemberDiffs);

public sealed record SolutionDiff(IReadOnlyList<TypeDiffEntry> Types)
{
    public int AddedCount => Types.Count(t => t.State == DiffState.Added);
    public int RemovedCount => Types.Count(t => t.State == DiffState.Removed);
    public int ModifiedCount => Types.Count(t => t.State == DiffState.Modified);
    public bool HasChanges => Types.Any(t => t.State != DiffState.Unchanged);
}

public static class SolutionDiffer
{
    public static SolutionDiff Diff(SolutionModel before, SolutionModel after)
    {
        var beforeTypes = before.Projects
            .SelectMany(p => p.Types)
            .GroupBy(t => t.Ref.FullyQualifiedName)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var afterTypes = after.Projects
            .SelectMany(p => p.Types)
            .GroupBy(t => t.Ref.FullyQualifiedName)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var results = new List<TypeDiffEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (fqn, at) in afterTypes)
        {
            seen.Add(fqn);
            if (!beforeTypes.TryGetValue(fqn, out var bt))
            {
                results.Add(new TypeDiffEntry(
                    at.Ref, at.Name, at.Namespace, DiffState.Added,
                    at.Members.Select(m => new MemberDiffEntry(m.Ref, m.Name, DiffState.Added)).ToList()));
                continue;
            }
            var memberDiffs = DiffMembers(bt, at);
            var hasMemberDiff = memberDiffs.Any(m => m.State != DiffState.Unchanged);
            if (hasMemberDiff)
            {
                results.Add(new TypeDiffEntry(at.Ref, at.Name, at.Namespace, DiffState.Modified, memberDiffs));
            }
        }

        foreach (var (fqn, bt) in beforeTypes)
        {
            if (seen.Contains(fqn)) continue;
            results.Add(new TypeDiffEntry(
                bt.Ref, bt.Name, bt.Namespace, DiffState.Removed,
                bt.Members.Select(m => new MemberDiffEntry(m.Ref, m.Name, DiffState.Removed)).ToList()));
        }

        return new SolutionDiff(results);
    }

    private static IReadOnlyList<MemberDiffEntry> DiffMembers(TypeModel before, TypeModel after)
    {
        var beforeBySig = before.Members
            .GroupBy(m => m.Ref.Signature)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var afterBySig = after.Members
            .GroupBy(m => m.Ref.Signature)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var results = new List<MemberDiffEntry>();
        foreach (var (sig, am) in afterBySig)
        {
            if (!beforeBySig.ContainsKey(sig))
            {
                results.Add(new MemberDiffEntry(am.Ref, am.Name, DiffState.Added));
            }
        }
        foreach (var (sig, bm) in beforeBySig)
        {
            if (!afterBySig.ContainsKey(sig))
            {
                results.Add(new MemberDiffEntry(bm.Ref, bm.Name, DiffState.Removed));
            }
        }
        return results;
    }
}
