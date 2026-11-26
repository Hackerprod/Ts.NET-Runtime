using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using TypeSharp.Hosting.HotReload;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public class HotReloadSourceReadTests
{
    private const string SeedSource = "function value(): int32 { return 1; }";
    private const string ReplacementSource = "function value(): int32 { return 2; }";

    [Fact]
    public async Task MissingSourceReportsErrorAndKeepsActiveGeneration()
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-reload-read-");
        try
        {
            string path = Path.Combine(directory.FullName, "app.ts");
            File.WriteAllText(path, SeedSource);
            await using var runtime = await new TypeSharpRuntimeBuilder().EnableHotReload().BuildAsync();
            Assert.True(await runtime.ReloadAsync(path));
            Assert.Equal(1, Assert.IsType<TsInt32Value>(runtime.Invoke("value")).Value);
            var original = runtime.ActiveGeneration;
            Assert.NotNull(original);

            var errors = new List<ModuleReloadErrorEventArgs>();
            var successes = new List<ModuleReloadedEventArgs>();
            runtime.ReloadError += (_, args) => errors.Add(args);
            runtime.ModuleReloaded += (_, args) => successes.Add(args);

            File.Delete(path);
            Assert.False(File.Exists(path));
            Assert.True(Directory.Exists(directory.FullName));
            Assert.False(await runtime.ReloadAsync(path));
            var error = Assert.Single(errors);
            Assert.Equal("app", error.ModuleName);
            Assert.Contains("app.ts", error.ErrorMessage);
            Assert.Empty(successes);
            Assert.Same(original, runtime.ActiveGeneration);
            Assert.Equal(1, Assert.IsType<TsInt32Value>(runtime.Invoke("value")).Value);

            File.WriteAllText(path, ReplacementSource);
            Assert.True(await runtime.ReloadAsync(path));
            var success = Assert.Single(successes);
            Assert.Equal("app", success.ModuleName);
            var replacement = runtime.ActiveGeneration;
            Assert.NotNull(replacement);
            Assert.NotSame(original, replacement);
            Assert.Equal(original.Id + 1, replacement.Id);
            Assert.Equal(replacement.Id, success.NewGenerationId);
            Assert.Single(errors);
            Assert.Equal(2, Assert.IsType<TsInt32Value>(runtime.Invoke("value")).Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExistingSourceWithPreCanceledTokenStillThrowsCancellation()
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-reload-read-");
        try
        {
            string path = Path.Combine(directory.FullName, "app.ts");
            File.WriteAllText(path, SeedSource);
            await using var runtime = await new TypeSharpRuntimeBuilder().EnableHotReload().BuildAsync();
            var original = runtime.ActiveGeneration;
            var errors = new List<ModuleReloadErrorEventArgs>();
            var successes = new List<ModuleReloadedEventArgs>();
            runtime.ReloadError += (_, args) => errors.Add(args);
            runtime.ModuleReloaded += (_, args) => successes.Add(args);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runtime.ReloadAsync(path, new CancellationToken(canceled: true)));
            Assert.Empty(errors);
            Assert.Empty(successes);
            Assert.Same(original, runtime.ActiveGeneration);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
