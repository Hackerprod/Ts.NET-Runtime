using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class KeyedCollectionObjectIdentityTests
{
    private static TsObject NewKey()
    {
        var key = new TsObject("Key");
        key.SetField("tag", TsValue.FromInt32(7));
        return key;
    }

    private static (TsObject Backing, TsValue Original, TsValue Alias, TsValue Distinct) Keys()
    {
        var backing = NewKey();
        var other = NewKey();
        var original = TsValue.FromObject(backing);
        var alias = TsValue.FromObject(backing);
        var distinct = TsValue.FromObject(other);

        Assert.NotSame(original, alias);
        Assert.Same(backing, Assert.IsType<TsObjectValue>(original).Value);
        Assert.Same(backing, Assert.IsType<TsObjectValue>(alias).Value);
        Assert.NotSame(backing, other);
        Assert.Same(other, Assert.IsType<TsObjectValue>(distinct).Value);
        Assert.Equal(backing.TypeName, other.TypeName);
        Assert.Equal(Assert.IsType<TsInt32Value>(backing.GetField("tag")).Value,
            Assert.IsType<TsInt32Value>(other.GetField("tag")).Value);
        return (backing, original, alias, distinct);
    }

    [Fact]
    public void SameWrapperAndDistinctObjectKeysRemainSeparate()
    {
        var (_, original, _, distinct) = Keys();
        var first = TsValue.FromObject(new TsObject("Payload"));
        var second = TsValue.FromObject(new TsObject("Payload"));
        var map = new TsMap();
        map.Set(original, first);
        Assert.True(map.Contains(original));
        Assert.Same(first, map.Get(original));
        Assert.False(map.Contains(distinct));
        map.Set(distinct, second);
        Assert.Equal(2, map.Count);
        Assert.Same(first, map.Get(original));
        Assert.Same(second, map.Get(distinct));

        var set = new TsSet();
        Assert.True(set.Add(original));
        Assert.True(set.Contains(original));
        Assert.False(set.Add(original));
        Assert.False(set.Contains(distinct));
        Assert.True(set.Add(distinct));
        Assert.Equal(2, set.Count);
        Assert.True(set.Contains(original));
        Assert.True(set.Contains(distinct));
    }

    [Fact]
    public void FreshWrappersForSameObjectShareMapAndSetKeys()
    {
        var (backing, original, alias, distinct) = Keys();
        var first = TsValue.FromObject(new TsObject("Payload"));
        var second = TsValue.FromObject(new TsObject("Payload"));
        var replacement = TsValue.FromObject(new TsObject("Payload"));
        var map = new TsMap();
        map.Set(original, first);
        Assert.True(map.Contains(alias), "C293_OBJECT_ALIAS_MISS");
        Assert.Same(first, map.Get(alias));
        map.Set(distinct, second);
        map.Set(alias, replacement);
        Assert.Equal(2, map.Count);
        Assert.Same(original, map.GetEntryAt(0).Key);
        Assert.Same(distinct, map.GetEntryAt(1).Key);
        Assert.Same(replacement, map.Get(original));
        Assert.Same(replacement, map.Get(alias));
        Assert.Same(second, map.Get(distinct));

        var removal = TsValue.FromObject(backing);
        Assert.NotSame(original, removal);
        Assert.NotSame(alias, removal);
        Assert.True(map.Remove(removal));
        Assert.Equal(1, map.Count);
        Assert.False(map.Contains(original));
        Assert.Same(distinct, map.GetEntryAt(0).Key);
        Assert.Same(second, map.Get(distinct));

        var set = new TsSet();
        Assert.True(set.Add(original));
        Assert.True(set.Add(distinct));
        Assert.True(set.Contains(alias));
        Assert.False(set.Add(alias));
        Assert.Equal(2, set.Count);
        Assert.Same(original, set.GetAt(0));
        Assert.Same(distinct, set.GetAt(1));
        Assert.True(set.Remove(removal));
        Assert.Equal(1, set.Count);
        Assert.False(set.Contains(original));
        Assert.True(set.Contains(distinct));
        Assert.Same(distinct, set.GetAt(0));
    }
}
