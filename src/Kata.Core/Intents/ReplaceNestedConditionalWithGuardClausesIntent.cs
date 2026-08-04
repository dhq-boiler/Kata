using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Replace Nested Conditional with Guard Clauses": convert an
// if / else where one branch is a single `return X;` (or `throw`) into a
// guard clause + un-indented body.
//
//   if (isSpecial) { return specialCase(); }
//   else { ... many statements ... }
//   →
//   if (isSpecial) return specialCase();
//   ... many statements ...   // hoisted out of the else, one indent shallower
//
// Also handles the mirror case (then is the long block, else is the guard),
// by inverting the condition. Selection points at the target IfStatementSyntax.
// Refuses when neither branch is a single return/throw.
public sealed record ReplaceNestedConditionalWithGuardClausesIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
}
