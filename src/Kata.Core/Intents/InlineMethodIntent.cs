using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Inline Method": replace a call to a small method with the method's
// body. MVP scope: the target method must be expression-bodied (single arrow
// expression) OR a block body with exactly one `return expr;` — anything
// more complex refuses with NotSupportedException. Only the ONE call site
// at the selection is inlined; the method declaration is left in place for
// the user to delete manually once every caller is inlined.
public sealed record InlineMethodIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
}
