using Kata.Core.Model;

namespace Kata.Core.Analysis;

// A detected code smell. Target is either a type (Member is null) or a specific member.
// Message is a short, human-readable explanation (may include measured values e.g. "45 lines").
//
// RelatedMembers: 「この smell に関連する他メンバー」の任意リスト。detector が知りうる
// group 情報 (DuplicatedCode の他の複製先、DataClumps の同じパラメータ束を共有する他
// メソッド、AlternativeClasses のペア相手、等) を後段 (AI 提案 prompt 等) が
// 参照できるように運ぶ。null / empty はこの smell に group 情報が無いことを示す。
public sealed record CodeSmell(
    SmellCategory Category,
    SmellSeverity Severity,
    TypeRef Type,
    MemberRef? Member,
    string Message,
    IReadOnlyList<MemberRef>? RelatedMembers = null);
