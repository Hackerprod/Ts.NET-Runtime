using TypeSharp.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public class DecimalNumericSeparatorIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("grouped-whole", "1_000_000", 1000000.0)]
    [InlineData("grouped-fraction", "12.5_0", 12.5)]
    [InlineData("grouped-exponent", "1.25e+0_2", 125.0)]
    [InlineData("plain-whole", "1000000", 1000000.0)]
    [InlineData("plain-fraction", "12.50", 12.5)]
    [InlineData("plain-exponent", "1.25e+02", 125.0)]
    [InlineData("prefixed-control", "0xFF_FF", 65535.0)]
    public async Task Invoke_ReturnsNativeDecimalNumericValue(string label, string spelling, double expected)
    {
        var directory = Directory.CreateTempSubdirectory("typesharp_c141_");
        try
        {
            var source = $"export function main(): number {{ return ({spelling}); }}";
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "app.ts"), source);
            output.WriteLine($"C141_HOST row={label} source={source}");
            var builder = new TypeSharpRuntimeBuilder().AddSourceDirectory(directory.FullName);

            var phase = "BuildAsync";
            try
            {
                await using var runtime = await builder.BuildAsync();
                output.WriteLine($"C141_HOST row={label} phase=BuildAsync completed");
                phase = "InvokeAsync";
                var actual = await runtime.InvokeAsync<double>("app", "main");
                output.WriteLine(FormattableString.Invariant(
                    $"C141_HOST row={label} phase=InvokeAsync completed expected={expected} actual={actual}"));
                phase = "Assert";
                Assert.Equal(expected, actual);
                phase = "DisposeAsync";
            }
            catch (Exception exception)
            {
                output.WriteLine($"C141_HOST row={label} phase={phase} exception={exception}");
                throw;
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
