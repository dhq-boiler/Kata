using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Extract Hierarchy": a class doing too many jobs gets a set of
// subclasses each representing a distinct special case. Distinct from
// ReplaceTypeCodeWithSubclasses in that the driver is polymorphism, not a
// type-code field — so instead of just creating empty shell subclasses, this
// intent also turns selected methods on the OwnerType into `abstract` and
// emits `override` stubs in every subclass.
//
// MethodsToVirtualize is optional. When empty, behaves exactly like #29
// (owner made abstract, shell subclasses). When populated, each named method
// on OwnerType loses its body + gains `abstract`, and every new subclass
// gets an `override` stub throwing NotImplementedException — a compile-safe
// scaffold for the user to fill in per case.
public sealed record ExtractHierarchyIntent : RefactoringIntent
{
    public required TypeRef OwnerType { get; init; }
    public required IReadOnlyList<string> SubclassNames { get; init; }
    public IReadOnlyList<MemberRef> MethodsToVirtualize { get; init; } = Array.Empty<MemberRef>();
    public NamespaceRef? TargetNamespace { get; init; }
}
