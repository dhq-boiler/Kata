namespace Kata.Core.Model;

public sealed record TypeModel(
    TypeRef Ref,
    string Name,
    NamespaceRef Namespace,
    TypeKind Kind,
    MemberAccessibility Accessibility,
    IReadOnlyList<MemberModel> Members,
    IReadOnlyList<TypeRef> BaseTypes,
    IReadOnlyList<TypeRef> ImplementedInterfaces,
    bool IsAbstract = false,
    bool IsStatic = false,
    bool IsGhost = false,
    bool IsForeignProject = false,
    // メンバー本体 (メソッド body) 内で参照している型の短名リスト。
    // SolutionGraphBuilder が uses エッジを引く際に、シグネチャ由来の参照に加えて
    // これも見に行く。C# は現在未実装、C++/CLI は BuildForeignProjectModels で body 走査。
    // null = 未計算 (uses への追加寄与なし)。
    IReadOnlyList<string>? BodyReferencedTypeNames = null);
