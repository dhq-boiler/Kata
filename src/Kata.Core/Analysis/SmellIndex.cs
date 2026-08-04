using Kata.Core.Model;

namespace Kata.Core.Analysis;

// Immutable lookup over detected smells. Kept as a side-index (not embedded in TypeModel /
// MemberModel) so language adapters can produce it independently of the source-of-truth model.
public sealed class SmellIndex
{
    public static SmellIndex Empty { get; } = new(Array.Empty<CodeSmell>());

    public SmellIndex(IReadOnlyList<CodeSmell> smells)
    {
        All = smells;

        var byType = new Dictionary<TypeRef, List<CodeSmell>>();
        var byMember = new Dictionary<MemberRef, List<CodeSmell>>();
        var typeLevel = new Dictionary<TypeRef, List<CodeSmell>>();

        foreach (var s in smells)
        {
            if (!byType.TryGetValue(s.Type, out var byTypeList))
                byType[s.Type] = byTypeList = new List<CodeSmell>();
            byTypeList.Add(s);

            if (s.Member is { } memberRef)
            {
                if (!byMember.TryGetValue(memberRef, out var byMemberList))
                    byMember[memberRef] = byMemberList = new List<CodeSmell>();
                byMemberList.Add(s);
            }
            else
            {
                if (!typeLevel.TryGetValue(s.Type, out var typeLevelList))
                    typeLevel[s.Type] = typeLevelList = new List<CodeSmell>();
                typeLevelList.Add(s);
            }
        }

        ByType = byType.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CodeSmell>)kv.Value);
        ByMember = byMember.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CodeSmell>)kv.Value);
        TypeLevelByType = typeLevel.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CodeSmell>)kv.Value);
    }

    public IReadOnlyList<CodeSmell> All { get; }
    public IReadOnlyDictionary<TypeRef, IReadOnlyList<CodeSmell>> ByType { get; }
    public IReadOnlyDictionary<MemberRef, IReadOnlyList<CodeSmell>> ByMember { get; }
    public IReadOnlyDictionary<TypeRef, IReadOnlyList<CodeSmell>> TypeLevelByType { get; }

    public IReadOnlyList<CodeSmell> ForType(TypeRef type) =>
        ByType.TryGetValue(type, out var list) ? list : Array.Empty<CodeSmell>();

    public IReadOnlyList<CodeSmell> ForMember(MemberRef member) =>
        ByMember.TryGetValue(member, out var list) ? list : Array.Empty<CodeSmell>();

    public IReadOnlyList<CodeSmell> TypeOnly(TypeRef type) =>
        TypeLevelByType.TryGetValue(type, out var list) ? list : Array.Empty<CodeSmell>();
}
