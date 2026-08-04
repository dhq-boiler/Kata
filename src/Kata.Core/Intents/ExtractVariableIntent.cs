using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Extract Variable" (a.k.a. "Introduce Explaining Variable"): lift
// an in-line expression into a named local so the code reads more directly.
// Selection must be a valid ExpressionSyntax inside the containing member's
// body — a `var {NewName} = {expr};` is inserted before the innermost
// enclosing statement, and the selected expression is replaced with an
// IdentifierName reference. Only the ONE selected occurrence is replaced
// (MVP — no "replace all occurrences" mode yet).
public sealed record ExtractVariableIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
    public required string NewVariableName { get; init; }
}
