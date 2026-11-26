using System;
using System.IO;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using Xunit;

namespace TypeSharp.Tests;

public sealed class StandardTsTemplateTests
{
    [Fact]
    public async Task ShippedStarterRunsInHostedRuntime()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "templates", "standard-ts", "src");
        var entry = Path.Combine(src, "main.ts");
        Assert.True(File.Exists(entry), $"Expected shipped template entry at '{entry}'.");

        await using var runtime = await new TypeSharpRuntimeBuilder()
            .AddSourceDirectory(src)
            .BuildAsync();
        Assert.Equal(42d, await runtime.InvokeAsync<double>("src", "main"));
    }
}
