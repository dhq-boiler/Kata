using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Extract Method": pull a chunk of code inside an existing method
// out into a separate method of its own. Depends on the code viewer (added
// 2026-08-01) for range picking — the class-diagram canvas alone can't say
// "this specific range of statements".
//
// SelectionStart / SelectionLength are absolute character offsets into the
// file identified by OwnerType.Member's DeclaringSyntaxReferences (i.e. the
// file text sent to the viewer). Roslyn's DataFlowAnalysis is then run on
// the selected span to infer parameters and return type — variables that
// flow in become parameters, a single variable flowing out becomes the
// return value, `void` when nothing flows out. Cases that are harder than
// that (multi-output, ref/out flows, control flow escaping the selection)
// throw NotSupportedException — the user then hand-massages.
public sealed record ExtractMethodIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required MemberRef ContainingMember { get; init; }
    public required int SelectionStart { get; init; }
    public required int SelectionLength { get; init; }
    public required string NewMethodName { get; init; }
}
