using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using Xunit;

namespace TypeSharp.Tests;

public sealed class HotReloadConfiguredSourceRootTests
{
    [Fact]
    public Task ReloadPreservesExplicitRootForNestedMainAndNamesake()
        => VerifyReloadAsync("nested");

    [Fact]
    public Task ReloadPreservesExplicitRootForNestedMainAndPeer()
        => VerifyReloadAsync("peer");

    private static async Task VerifyReloadAsync(string siblingStem)
    {
        var root = Directory.CreateTempSubdirectory("ts-hot-root-");
        try
        {
            var nested = root.CreateSubdirectory("nested");
            var mainPath = Path.Combine(nested.FullName, "main.ts");
            var siblingPath = Path.Combine(nested.FullName, siblingStem + ".ts");
            var siblingId = "nested/" + siblingStem;
            await File.WriteAllTextAsync(mainPath, "export function currentValue(): int32 { return 1; }");
            await File.WriteAllTextAsync(siblingPath, "export function siblingValue(): int32 { return 9; }");

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(root.FullName)
                .EnableHotReload()
                .ConfigureHotReload(o => o.DebounceInterval = Timeout.InfiniteTimeSpan)
                .BuildAsync();

            var before = runtime.ActiveGeneration;
            Assert.NotNull(before);
            var expectedIds = new[] { "nested", siblingId };
            Assert.Equal(expectedIds, before.Modules.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            Assert.Equal(1, await runtime.InvokeAsync<int>("nested", "currentValue"));
            Assert.Equal(9, await runtime.InvokeAsync<int>(siblingId, "siblingValue"));

            var errors = new List<string>();
            runtime.ReloadError += (_, args) => errors.Add(args.ErrorMessage);
            await File.WriteAllTextAsync(mainPath, "export function currentValue(): int32 { return 2; }");
            var reloaded = await runtime.ReloadAsync(mainPath);
            Assert.True(reloaded, string.Join(Environment.NewLine, errors));
            Assert.Empty(errors);

            var after = runtime.ActiveGeneration;
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.Equal(expectedIds, after.Modules.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            Assert.Equal(2, await runtime.InvokeAsync<int>("nested", "currentValue"));
            Assert.Equal(9, await runtime.InvokeAsync<int>(siblingId, "siblingValue"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
