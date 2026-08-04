using Kata.Core.Model;

namespace Kata.Core.Intents;

// Fowler "Tease Apart Inheritance": you have an inheritance hierarchy that
// is doing two jobs at once (e.g. Deal ⟨EquityBid, EquityAsk, BondBid, BondAsk⟩
// where the axes are Instrument × Side). Split the SECOND axis into its own
// hierarchy, then use delegation from the primary root to invoke it. The
// mechanical part is scaffold-only:
//   * a new abstract SecondaryHierarchyName class file
//   * one shell subclass file per SecondarySubclassNames entry
//   * a `protected SecondaryType? DelegationFieldName;` field appended to
//     PrimaryHierarchyRoot
// Deleting the redundant subclasses in the primary hierarchy and moving
// per-secondary-axis methods into the new subclasses is left to the user
// (typically via existing Push Down Method / Move Method refactors).
public sealed record TeaseApartInheritanceIntent : RefactoringIntent
{
    public required TypeRef PrimaryHierarchyRoot { get; init; }
    public required string SecondaryHierarchyName { get; init; }
    public required IReadOnlyList<string> SecondarySubclassNames { get; init; }
    public required string DelegationFieldName { get; init; }
    public NamespaceRef? TargetNamespace { get; init; }
}
