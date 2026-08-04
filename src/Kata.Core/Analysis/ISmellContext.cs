using Kata.Core.Model;

namespace Kata.Core.Analysis;

/// <summary>
/// 言語非依存 detector (<see cref="IUniversalSmellDetector"/>) に渡す情報源。
///
/// 各言語 adapter (Roslyn 側 / Cpp 側) がこの interface を実装し、universal detector が
/// <see cref="HandwrittenTypes"/> を走査して smell を検出する。body 系 detector は
/// <see cref="GetBodyText"/> / <see cref="GetBodyLineCount"/> を呼び、adapter は該当言語の
/// syntax tree / source から実装する。
///
/// 「Roslyn 固有 API を使う detector」は本 interface では扱わず、
/// <see cref="Kata.Roslyn"/> 内部の IRoslynSmellDetector として残置する。
/// </summary>
public interface ISmellContext
{
    SolutionModel Model { get; }

    /// <summary>
    /// この context がカバーする言語 ("csharp" / "cpp-cli" 等)。
    /// 混在プロジェクトでも各 adapter が自言語分だけを返す想定。
    /// </summary>
    string LanguageId { get; }

    /// <summary>
    /// 手書きされた (= ghost や自動生成でない) 型を列挙する。
    /// Roslyn 側は Compilation に載っている C# 型のみ、Cpp 側は cpp-cli プロジェクトに
    /// 属す型のみを返す。
    /// </summary>
    IEnumerable<TypeModel> HandwrittenTypes { get; }

    /// <summary>
    /// メソッド / コンストラクタの body 文字列を返す。body の無いメンバー
    /// (abstract / partial / フィールド等) や、body 取得に失敗したら null を返す。
    /// </summary>
    string? GetBodyText(MemberRef member);

    /// <summary>
    /// body の行数を返す。body が無いなら 0。
    /// </summary>
    int GetBodyLineCount(MemberRef member);

    /// <summary>
    /// 型全体の source テキスト (宣言ファイルの全文 or 該当ノードのテキスト)。
    /// RefusedBequest / TemporaryField / DuplicatedCode のような、型全体を
    /// テキストで走査したい detector 用。取得失敗時は null。
    /// </summary>
    string? GetTypeText(TypeRef type);

    /// <summary>
    /// TypeRef で TypeModel を引く。BaseTypes を辿る RefusedBequest 等で使う。
    /// </summary>
    bool TryGetType(TypeRef typeRef, out TypeModel? type);
}
