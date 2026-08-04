using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Decompose Conditional": break a complicated `if / else` into three
// intention-revealing methods — one that computes the condition, one for the
// then-block, one for the else-block. Result at the call site:
//   if (ConditionMethodName()) { ThenMethodName(); } else { ElseMethodName(); }
//
// Selection points at an IfStatementSyntax. DataFlowAnalysis on the
// condition / then / else provides parameters (locals + parameters read
// from outer scope). MVP scope: void then / void else (no return values
// flowing out); refuses if either branch has multi-output data flow.
public sealed record DecomposeConditionalIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
    public required string ConditionMethodName { get; init; }
    public required string ThenMethodName { get; init; }
    public string? ElseMethodName { get; init; }
}
