using TypeSharp.Hosting;
using Xunit;

namespace TypeSharp.Tests;

public sealed class Uint8ArrayHostingContractsTests
{
    [Fact]
    public async Task InvokeManagedByteArrayReturnsOnlyAnIndependentLogicalWindow()
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-uint8array-host-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "app.ts"), """
                export function observe(b: Uint8Array): Uint8Array {
                    const v=b.subarray(1,5).subarray(1,3);
                    b[2]=b[2]+11;
                    v.reverse();
                    v[0]=79;
                    return v;
                }
                export function root(b: Uint8Array): Uint8Array { return b; }
                """);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(directory.FullName).BuildAsync();
            byte[] first = [10, 20, 30, 40, 50], second = [1, 2, 3, 4, 5];
            var window = await runtime.InvokeAsync<byte[]>("app", "observe", first);
            Assert.Equal(new byte[] { 79, 41 }, window);
            window[0] = 255;
            Assert.Equal(new byte[] { 79, 41 }, Assert.IsType<byte[]>(await runtime.InvokeAsync<object>("app", "observe", first)));
            var next = await runtime.InvokeAsync<byte[]>("app", "observe", second);
            Assert.Equal(new byte[] { 79, 14 }, next);
            Assert.NotSame(window, next);
            var owner = await runtime.InvokeAsync<byte[]>("app", "root", first);
            Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, owner);
            Assert.NotSame(first, owner);
            Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, first);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, second);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
