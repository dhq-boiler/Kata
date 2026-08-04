using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Consolidate Conditional Expression": a run of `if` statements
// that all execute the same body collapse into one guarded by the OR of
// their conditions.
//
//   if (isDead) return 0;
//   if (isSeparated) return 0;
//   if (isRetired) return 0;
//   return realValue;
// →
//   if (isDead || isSeparated || isRetired) return 0;
//   return realValue;
//
// Selection covers 2+ consecutive if-statements at the same block level.
// MVP: each if must have no else clause AND all bodies must be
// syntactically identical (normalized text match). Anything else refuses.
public sealed record ConsolidateConditionalExpressionIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
}
