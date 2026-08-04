using Kata.Core.Diff;
using Kata.Core.Model;

namespace Kata.Tests;

public sealed class SolutionDifferTests
{
    private static readonly NamespaceRef Ns = new("Ns");

    private static TypeModel MakeType(string name, params (string sig, string memberName)[] members)
    {
        var typeRef = new TypeRef($"Ns.{name}");
        var memberModels = members
            .Select(m => new MemberModel(
                Ref: new MemberRef(typeRef, m.sig),
                Name: m.memberName,
                Kind: MemberKind.Method,
                Accessibility: MemberAccessibility.Public,
                ReturnTypeDisplay: "void",
                IsStatic: false,
                Parameters: Array.Empty<ParameterModel>()))
            .ToList();
        return new TypeModel(
            Ref: typeRef,
            Name: name,
            Namespace: Ns,
            Kind: TypeKind.Class,
            Accessibility: MemberAccessibility.Public,
            Members: memberModels,
            BaseTypes: Array.Empty<TypeRef>(),
            ImplementedInterfaces: Array.Empty<TypeRef>());
    }

    private static SolutionModel MakeSolution(params TypeModel[] types)
        => new("dummy.slnx", new[]
        {
            new ProjectModel("MyLib", "MyLib.csproj", "csharp", types),
        });

    [Fact]
    public void Added_type_is_reported()
    {
        var before = MakeSolution(MakeType("Foo"));
        var after = MakeSolution(MakeType("Foo"), MakeType("Bar"));
        var diff = SolutionDiffer.Diff(before, after);

        var bar = diff.Types.Single(t => t.Name == "Bar");
        Assert.Equal(DiffState.Added, bar.State);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
    }

    [Fact]
    public void Removed_type_is_reported()
    {
        var before = MakeSolution(MakeType("Foo"), MakeType("Bar"));
        var after = MakeSolution(MakeType("Foo"));
        var diff = SolutionDiffer.Diff(before, after);

        var bar = diff.Types.Single(t => t.Name == "Bar");
        Assert.Equal(DiffState.Removed, bar.State);
        Assert.Equal(0, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
    }

    [Fact]
    public void Modified_type_shows_member_level_diff()
    {
        var before = MakeSolution(MakeType("Foo", ("OldOnly()", "OldOnly"), ("Common()", "Common")));
        var after = MakeSolution(MakeType("Foo", ("NewOnly()", "NewOnly"), ("Common()", "Common")));
        var diff = SolutionDiffer.Diff(before, after);

        var foo = diff.Types.Single(t => t.Name == "Foo");
        Assert.Equal(DiffState.Modified, foo.State);

        var newOnly = foo.MemberDiffs.Single(m => m.Name == "NewOnly");
        Assert.Equal(DiffState.Added, newOnly.State);

        var oldOnly = foo.MemberDiffs.Single(m => m.Name == "OldOnly");
        Assert.Equal(DiffState.Removed, oldOnly.State);

        Assert.DoesNotContain(foo.MemberDiffs, m => m.Name == "Common");
    }

    [Fact]
    public void Identical_solutions_produce_no_changes()
    {
        var t = MakeType("Foo", ("A()", "A"), ("B()", "B"));
        var before = MakeSolution(t);
        var after = MakeSolution(t);
        var diff = SolutionDiffer.Diff(before, after);
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.Types);
    }

    [Fact]
    public void Added_type_reports_all_members_as_added()
    {
        var before = MakeSolution();
        var after = MakeSolution(MakeType("New", ("A()", "A"), ("B()", "B")));
        var diff = SolutionDiffer.Diff(before, after);

        var t = diff.Types.Single();
        Assert.Equal(DiffState.Added, t.State);
        Assert.Equal(2, t.MemberDiffs.Count);
        Assert.All(t.MemberDiffs, m => Assert.Equal(DiffState.Added, m.State));
    }
}
