using System;
using System.Threading.Tasks;
using TypeSharp.Interop.Marshalling;
using TypeSharp.VM.Memory;
using Xunit;
namespace TypeSharp.Tests;

public sealed class DerivedManagedTaskResultTests
{
    private sealed class IntTask : Task<int> { public IntTask() : base(() => 42) { } }
    private sealed class PlainTask : Task { public PlainTask() : base(() => { }) { } }
    private sealed class TaggedPlainTask<TTag> : Task { public TaggedPlainTask() : base(() => { }) { } }

    private static T Started<T>(T task) where T : Task
    {
        task.RunSynchronously(TaskScheduler.Default);
        Assert.True(task.IsCompletedSuccessfully);
        return task;
    }

    private static Task<TsValue> AwaitManaged(object value) =>
        Marshaller.AwaitAsync(Assert.IsType<TsPromiseValue>(Marshaller.FromManaged(value)));

    [Fact] public async Task ConcreteResult()
    {
        var value = await AwaitManaged(Started(new IntTask()));
        Assert.True(value is TsInt32Value { Value: 42 }, $"concrete: expected Int32(42); actual {value.GetType().Name}");
    }
    [Fact] public async Task TaggedPlain()
    {
        var value = await AwaitManaged(Started(new TaggedPlainTask<string>()));
        Assert.True(value is TsVoid, $"tagged: expected TsVoid; actual {value.GetType().Name}");
    }
    [Fact] public async Task OrdinaryResult() =>
        Assert.Equal(19, Assert.IsType<TsInt32Value>(await AwaitManaged(Task.FromResult(19))).Value);
    [Fact] public async Task CompletedVoid() => Assert.IsType<TsVoid>(await AwaitManaged(Task.CompletedTask));
    [Fact] public async Task PlainVoid() => Assert.IsType<TsVoid>(await AwaitManaged(Started(new PlainTask())));
    [Fact] public async Task ValueTaskResult() =>
        Assert.Equal(73, Assert.IsType<TsInt32Value>(await AwaitManaged(new ValueTask<int>(73))).Value);
    [Fact] public async Task FaultIdentity()
    {
        var error = new InvalidOperationException("managed task sentinel");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
            async () => { await AwaitManaged(Task.FromException<int>(error)); }));
    }
}
