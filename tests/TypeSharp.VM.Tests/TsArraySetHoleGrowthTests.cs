using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class TsArraySetHoleGrowthTests
{
    [Fact]
    public void SetHoleDoesNotResurrectRemovedSparseValue()
    {
        var array = new TsArray(4);
        var anchor = new TsInt32Value(11);
        array.Add(anchor);
        array.Set(2, new TsInt32Value(99));
        Assert.True(array.IsHole(1));
        Assert.False(array.IsHole(2));
        array.RemoveLast();
        Assert.Equal(2, array.Count);
        Assert.Equal(4, array.Capacity);
        array.SetHole(3);

        Assert.Equal(4, array.Count);
        Assert.Equal(4, array.Capacity);
        Assert.False(array.IsHole(0));
        Assert.Same(anchor, array.Get(0));
        Assert.True(array.IsHole(1));
        Assert.Same(TsValue.Void, array.Get(1));
        Assert.True(array.IsHole(3));
        Assert.Same(TsValue.Void, array.Get(3));
        Assert.True(array.IsHole(2), "SetHole growth must not resurrect the removed sparse element at index 2.");
        Assert.Same(TsValue.Void, array.Get(2));
    }

    [Fact]
    public void GaplessAppendClearsFormerTarget()
    {
        var array = new TsArray(4);
        array.Add(new TsInt32Value(11));
        array.Set(2, new TsInt32Value(99));
        array.RemoveLast();
        array.SetHole(2);
        Assert.Equal(3, array.Count);
        Assert.Equal(4, array.Capacity);
        Assert.Equal(11, Assert.IsType<TsInt32Value>(array.Get(0)).Value);
        Assert.True(array.IsHole(1));
        Assert.True(array.IsHole(2));
        Assert.Same(TsValue.Void, array.Get(2));
    }

    [Fact]
    public void FirstPresenceAllocationPreservesDensePrefix()
    {
        var array = new TsArray(4);
        array.Add(new TsInt32Value(11));
        array.Add(TsValue.Void);
        array.SetHole(3);
        Assert.Equal(4, array.Count);
        Assert.Equal(4, array.Capacity);
        Assert.False(array.IsHole(0));
        Assert.Equal(11, Assert.IsType<TsInt32Value>(array.Get(0)).Value);
        Assert.False(array.IsHole(1));
        Assert.Same(TsValue.Void, array.Get(1));
        Assert.True(array.IsHole(2));
        Assert.True(array.IsHole(3));
        Assert.Same(TsValue.Void, array.Get(2));
        Assert.Same(TsValue.Void, array.Get(3));
    }

    [Fact]
    public void InteriorHolePreservesLiveNeighborsAndExplicitVoid()
    {
        var array = new TsArray(4);
        array.Add(new TsInt32Value(11));
        array.Add(new TsInt32Value(99));
        array.Add(TsValue.Void);
        array.SetHole(1);
        Assert.Equal(3, array.Count);
        Assert.Equal(4, array.Capacity);
        Assert.False(array.IsHole(0));
        Assert.Equal(11, Assert.IsType<TsInt32Value>(array.Get(0)).Value);
        Assert.True(array.IsHole(1));
        Assert.Same(TsValue.Void, array.Get(1));
        Assert.False(array.IsHole(2));
        Assert.Same(TsValue.Void, array.Get(2));
    }

    [Fact]
    public void ForcedBackingGrowthDoesNotResurrectFormerValue()
    {
        var array = new TsArray(4);
        array.Add(new TsInt32Value(11));
        array.Set(2, new TsInt32Value(99));
        Assert.False(array.IsHole(2));
        array.RemoveLast();
        var previousCapacity = array.Capacity;
        array.SetHole(4);
        Assert.Equal(5, array.Count);
        Assert.True(array.Capacity > previousCapacity);
        Assert.False(array.IsHole(0));
        Assert.Equal(11, Assert.IsType<TsInt32Value>(array.Get(0)).Value);
        for (var index = 1; index < 5; index++)
        {
            Assert.True(array.IsHole(index));
            Assert.Same(TsValue.Void, array.Get(index));
        }
    }
}
