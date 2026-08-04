using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Convert Procedural Design to Objects": procedures that keep taking
// the same data record as their first parameter are the data record's
// methods in disguise. Move them onto the record.
//
// For each method in MethodsToMove:
//   * verify the first parameter's type matches DataRecordType (skip if not);
//   * copy the method to DataRecordType with the first parameter dropped;
//   * rewrite body references to the dropped parameter's identifier as
//     `this` (so `data.Name` → `Name`, bare `data` → `this`);
//   * remove the method from ProceduralClass;
//   * rewrite call sites: `Proc.M(record, x)` → `record.M(x)`.
public sealed record ConvertProceduralToObjectsIntent : RefactoringIntent
{
    public required TypeRef ProceduralClass { get; init; }
    public required TypeRef DataRecordType { get; init; }
    public required IReadOnlyList<MemberRef> MethodsToMove { get; init; }
}
