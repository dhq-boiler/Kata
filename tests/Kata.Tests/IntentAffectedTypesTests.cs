using Kata.Core.Intents;
using Kata.Core.Model;

namespace Kata.Tests;

public sealed class IntentAffectedTypesTests
{
    private static readonly TypeRef Foo = new("Ns.Foo");
    private static readonly TypeRef Bar = new("Ns.Bar");
    private static readonly TypeRef Baz = new("Ns.Baz");

    [Fact]
    public void Rename_returns_target_type()
    {
        var intent = IntentFactory.Rename(Foo, "Renamed", IntentSource.Human);
        Assert.Equal(new[] { Foo }, IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void ExtractInterface_returns_source_type()
    {
        var intent = IntentFactory.ExtractInterface(Foo, System.Array.Empty<MemberRef>(), "IFoo", IntentSource.Human);
        Assert.Equal(new[] { Foo }, IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void CollapseHierarchy_returns_both_subclass_and_parent()
    {
        var intent = IntentFactory.CollapseHierarchy(Foo, Bar, IntentSource.Human);
        Assert.Equal(new[] { Foo, Bar }, IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void PullUpMethod_returns_subclass_and_parent()
    {
        var intent = IntentFactory.PullUpMethod(Foo, Bar, new[] { new MemberRef(Foo, "M()") }, IntentSource.Human);
        Assert.Equal(new[] { Foo, Bar }, IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void PushDownField_returns_parent_and_subclass()
    {
        var intent = IntentFactory.PushDownField(Foo, Bar, new[] { new MemberRef(Foo, "field") }, IntentSource.Human);
        Assert.Equal(new[] { Foo, Bar }, IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void RenameField_returns_owner_type()
    {
        var intent = IntentFactory.RenameField(Foo, new MemberRef(Foo, "field"), "Field", IntentSource.Human);
        Assert.Equal(new[] { Foo }, IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void AddGhostType_returns_empty_since_no_existing_type_is_affected()
    {
        var intent = IntentFactory.AddGhostType("Newbie", new NamespaceRef("Ns"), TypeKind.Class, IntentSource.Human);
        Assert.Empty(IntentAffectedTypes.Extract(intent));
    }

    [Fact]
    public void RemoveSubclass_returns_subclass_and_replacement_base()
    {
        var intent = IntentFactory.RemoveSubclass(Foo, Bar, IntentSource.Human);
        Assert.Equal(new[] { Foo, Bar }, IntentAffectedTypes.Extract(intent));
    }
}
