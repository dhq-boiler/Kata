using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Inline Variable" (a.k.a. "Inline Temp"): replace every use of a
// local variable with its initializer expression, and delete the declaration.
// MVP scope: the local must be a `var` (or typed) with an initializer AND
// never be reassigned inside the enclosing method — otherwise substitution
// would silently change semantics. Selection may point at either the
// declaration itself or any use of the local.
public sealed record InlineVariableIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
}
