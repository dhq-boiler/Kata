using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Introduce Assertion": document an assumption in code by adding an
// explicit precondition check. In Kata: inserts
//   System.Diagnostics.Debug.Assert(condition, "message");
// as a new statement at the top of the block that contains the caret
// position — typically the beginning of the containing method, but users
// can plant assertions inside inner blocks too by placing the caret there.
public sealed record IntroduceAssertionIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required string AssertionExpression { get; init; }
    public string? Message { get; init; }
}
