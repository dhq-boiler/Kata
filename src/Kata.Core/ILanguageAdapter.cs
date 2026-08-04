using Kata.Core.Analysis;
using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.Core;

public interface ILanguageAdapter
{
    string LanguageId { get; }

    IReadOnlyCollection<Type> SupportedIntentTypes { get; }

    Task<SolutionModel> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);

    Task<ChangeSet> ProposeChangesAsync(
        SolutionModel model,
        IReadOnlyList<RefactoringIntent> intents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Write the change set to disk AND incrementally update the adapter's internal
    /// model so no disk reload is needed. Returns the new <see cref="SolutionModel"/>
    /// so callers can rebuild views without re-opening the workspace.
    /// </summary>
    Task<SolutionModel> ApplyChangesAsync(ChangeSet changeSet, CancellationToken cancellationToken = default);

    Task<MemberSource?> GetMemberSourceAsync(
        SolutionModel model,
        TypeRef ownerType,
        MemberRef member,
        CancellationToken cancellationToken = default);

    Task<(TypeRef OwnerType, MemberRef Member)?> ResolveMemberAtAsync(
        SolutionModel model,
        TypeRef contextOwnerType,
        MemberRef contextMember,
        int offsetInSource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find every source occurrence of the given symbol across all loaded
    /// projects. When <paramref name="member"/> is null the target is the type
    /// itself; when non-null it is a specific member of that type. Cross-language
    /// results are merged (C# via Roslyn SymbolFinder, C++/CLI via the Cpp index).
    /// </summary>
    Task<IReadOnlyList<ReferenceLocation>> FindReferencesAsync(
        SolutionModel model,
        TypeRef ownerType,
        MemberRef? member,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Statically analyze the loaded model for code smells (Fowler, 24 categories).
    /// Returns an index that can be looked up by TypeRef or MemberRef. Adapters that
    /// don't implement analysis for a language should return <see cref="SmellIndex.Empty"/>.
    /// </summary>
    Task<SmellIndex> DetectSmellsAsync(
        SolutionModel model,
        CancellationToken cancellationToken = default);
}
