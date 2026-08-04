using Kata.Core.Model;

namespace Kata.Core.Diff;

public enum DiffState
{
    Unchanged,
    Added,
    Removed,
    Modified,
    Moved,   // Extract Class / Move Method 等で他型に移動した member 用
}

public enum MoveConfidence
{
    Exact,     // Ref.Signature 完全一致
    NameArity, // 同名 + 同 arity (return / param 型が変わったパターン)
    NameOnly,  // 同名のみ (overload 変更等)
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

// Removed-from-A + Added-to-B の pair 相関で検出される「移動」1 件。
// FromType/ToType の位置に応じて overlay 上に「«moved»」の破線 edge を張るのに使う。
public sealed record MemberMove(
    MemberRef FromMember, TypeRef FromType, int FromIndex,
    MemberRef ToMember,   TypeRef ToType,   int ToIndex,
    string DisplayName,
    MoveConfidence Confidence);

public sealed record SolutionDiff(
    IReadOnlyList<TypeDiffEntry> Types,
    IReadOnlyList<MemberMove> Moves)
{
    // 旧 ctor 互換のため (Moves 引数なしで呼ばれても空 list 扱い)
    public SolutionDiff(IReadOnlyList<TypeDiffEntry> types) : this(types, Array.Empty<MemberMove>()) { }

    public int AddedCount => Types.Count(t => t.State == DiffState.Added);
    public int RemovedCount => Types.Count(t => t.State == DiffState.Removed);
    public int ModifiedCount => Types.Count(t => t.State == DiffState.Modified);
    public bool HasChanges => Types.Any(t => t.State != DiffState.Unchanged) || Moves.Count > 0;
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

        // Move 検出用の raw な (removed / added) member 群を collect (type 別 index 付き)
        var removedCandidates = new List<(TypeRef Type, MemberRef Ref, string Name, int Index)>();
        var addedCandidates   = new List<(TypeRef Type, MemberRef Ref, string Name, int Index)>();

        foreach (var (fqn, at) in afterTypes)
        {
            seen.Add(fqn);
            if (!beforeTypes.TryGetValue(fqn, out var bt))
            {
                var added = at.Members.Select(m => new MemberDiffEntry(m.Ref, m.Name, DiffState.Added)).ToList();
                results.Add(new TypeDiffEntry(at.Ref, at.Name, at.Namespace, DiffState.Added, added));
                // 新規型でも member 単位で他型から移ってきた可能性はあるので Move 候補に含める
                for (int i = 0; i < at.Members.Count; i++)
                    addedCandidates.Add((at.Ref, at.Members[i].Ref, at.Members[i].Name, i));
                continue;
            }
            var (memberDiffs, removedInThis, addedInThis) = DiffMembers(bt, at);
            foreach (var e in removedInThis)
                removedCandidates.Add((bt.Ref, e.Ref, e.Name, e.Index));
            foreach (var e in addedInThis)
                addedCandidates.Add((at.Ref, e.Ref, e.Name, e.Index));

            var hasMemberDiff = memberDiffs.Any(m => m.State != DiffState.Unchanged);
            if (hasMemberDiff)
            {
                results.Add(new TypeDiffEntry(at.Ref, at.Name, at.Namespace, DiffState.Modified, memberDiffs));
            }
        }

        foreach (var (fqn, bt) in beforeTypes)
        {
            if (seen.Contains(fqn)) continue;
            var removed = bt.Members.Select(m => new MemberDiffEntry(m.Ref, m.Name, DiffState.Removed)).ToList();
            results.Add(new TypeDiffEntry(bt.Ref, bt.Name, bt.Namespace, DiffState.Removed, removed));
            // 消滅型からの member 移動も検出対象に (Extract Class 由来の source が丸ごと消えるケース)
            for (int i = 0; i < bt.Members.Count; i++)
                removedCandidates.Add((bt.Ref, bt.Members[i].Ref, bt.Members[i].Name, i));
        }

        // === Move 相関 ===
        // Pass 1: Signature 完全一致 (Exact confidence)
        var moves = new List<MemberMove>();
        var consumedRemoved = new HashSet<int>();
        var consumedAdded = new HashSet<int>();
        for (int i = 0; i < removedCandidates.Count; i++)
        {
            var r = removedCandidates[i];
            for (int j = 0; j < addedCandidates.Count; j++)
            {
                if (consumedAdded.Contains(j)) continue;
                var a = addedCandidates[j];
                // 同型内での name reuse (Rename ではない普通の再定義) は Move にしない
                if (r.Type.FullyQualifiedName == a.Type.FullyQualifiedName) continue;
                if (!string.Equals(r.Ref.Signature, a.Ref.Signature, StringComparison.Ordinal)) continue;

                moves.Add(new MemberMove(
                    r.Ref, r.Type, r.Index,
                    a.Ref, a.Type, a.Index,
                    r.Name,
                    MoveConfidence.Exact));
                consumedRemoved.Add(i);
                consumedAdded.Add(j);
                break;
            }
        }

        // Move 相関で「消化された」removed/added を MemberDiffEntry から降ろす:
        // 消化した member は added/removed ではなく Moved 状態にする。
        // 具体的には: results に既に登録済みの TypeDiffEntry.MemberDiffs を patch。
        if (moves.Count > 0)
        {
            var consumedFromKeys = new HashSet<(string TypeFqn, string Sig)>();
            var consumedToKeys   = new HashSet<(string TypeFqn, string Sig)>();
            foreach (var m in moves)
            {
                consumedFromKeys.Add((m.FromType.FullyQualifiedName, m.FromMember.Signature));
                consumedToKeys.Add((m.ToType.FullyQualifiedName, m.ToMember.Signature));
            }

            for (int t = 0; t < results.Count; t++)
            {
                var td = results[t];
                var typeFqn = td.Ref.FullyQualifiedName;
                var patched = td.MemberDiffs.Select(md =>
                {
                    if (md.State == DiffState.Removed && consumedFromKeys.Contains((typeFqn, md.Ref.Signature)))
                        return md with { State = DiffState.Moved };
                    if (md.State == DiffState.Added && consumedToKeys.Contains((typeFqn, md.Ref.Signature)))
                        return md with { State = DiffState.Moved };
                    return md;
                }).ToList();
                results[t] = td with { MemberDiffs = patched };
            }
        }

        return new SolutionDiff(results, moves);
    }

    // 返り値: (全 member の diff entries, added だけの位置情報, removed だけの位置情報)
    private static (IReadOnlyList<MemberDiffEntry>, List<(MemberRef Ref, string Name, int Index)> Removed, List<(MemberRef Ref, string Name, int Index)> Added)
        DiffMembers(TypeModel before, TypeModel after)
    {
        var beforeBySig = new Dictionary<string, (MemberRef Ref, string Name, int Index)>(StringComparer.Ordinal);
        for (int i = 0; i < before.Members.Count; i++)
        {
            var m = before.Members[i];
            if (!beforeBySig.ContainsKey(m.Ref.Signature))
                beforeBySig[m.Ref.Signature] = (m.Ref, m.Name, i);
        }
        var afterBySig = new Dictionary<string, (MemberRef Ref, string Name, int Index)>(StringComparer.Ordinal);
        for (int i = 0; i < after.Members.Count; i++)
        {
            var m = after.Members[i];
            if (!afterBySig.ContainsKey(m.Ref.Signature))
                afterBySig[m.Ref.Signature] = (m.Ref, m.Name, i);
        }

        var results = new List<MemberDiffEntry>();
        var added = new List<(MemberRef Ref, string Name, int Index)>();
        var removed = new List<(MemberRef Ref, string Name, int Index)>();

        foreach (var (sig, am) in afterBySig)
        {
            if (!beforeBySig.ContainsKey(sig))
            {
                results.Add(new MemberDiffEntry(am.Ref, am.Name, DiffState.Added));
                added.Add(am);
            }
        }
        foreach (var (sig, bm) in beforeBySig)
        {
            if (!afterBySig.ContainsKey(sig))
            {
                results.Add(new MemberDiffEntry(bm.Ref, bm.Name, DiffState.Removed));
                removed.Add(bm);
            }
        }
        return (results, removed, added);
    }
}
