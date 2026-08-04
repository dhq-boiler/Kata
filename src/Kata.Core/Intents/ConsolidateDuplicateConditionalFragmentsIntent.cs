using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Consolidate Duplicate Conditional Fragments": if the SAME line of
// code appears at the top or bottom of every branch of an if-statement,
// hoist it out so it runs unconditionally.
//
//   if (isSpecialDeal()) { total = price * 0.95; send(); }
//   else                 { total = price * 0.98; send(); }
// →
//   if (isSpecialDeal()) { total = price * 0.95; }
//   else                 { total = price * 0.98; }
//   send();
//
// Selection points at the target IfStatementSyntax. MVP requires an else
// branch — no-else ifs have nothing to consolidate against. Prefix and
// suffix hoisting are both attempted; refuses if neither finds any duplicate.
public sealed record ConsolidateDuplicateConditionalFragmentsIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
}
