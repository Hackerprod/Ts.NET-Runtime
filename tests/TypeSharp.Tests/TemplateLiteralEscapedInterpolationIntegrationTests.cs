using System.Text.Json;
using TypeSharp.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class TemplateLiteralEscapedInterpolationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public Task EscapedDollarStaysLiteralWhileUnescapedDollarSubstitutes() => Verify(
        nameof(EscapedDollarStaysLiteralWhileUnescapedDollarSubstitutes),
        """
        function main(label: string): string {
            return `draft:\${label} / live:${label}`;
        }
        """,
        """draft:${label} / live:ready""");

    [Fact]
    public Task OrdinaryInterpolationStillSubstitutes() => Verify(
        nameof(OrdinaryInterpolationStillSubstitutes),
        """
        function main(label: string): string {
            return `live:${label}`;
        }
        """,
        """live:ready""");

    [Fact]
    public Task EscapedBackslashDoesNotEscapeFollowingInterpolation() => Verify(
        nameof(EscapedBackslashDoesNotEscapeFollowingInterpolation),
        """
        function main(label: string): string {
            return `live:\\${label}`;
        }
        """,
        """live:\ready""");

    private async Task Verify(string row, string source, string expected)
    {
        var dir = Directory.CreateTempSubdirectory("typesharp-escaped-interpolation-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "app.ts"), source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
            var actual = await runtime.InvokeAsync<string>("app", "main", "ready");
            var detail = $"row={row}; BuildAsync=success; InvokeAsync<string>=success; expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual)}";
            output.WriteLine(detail);
            Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"Untagged template value mismatch: {detail}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
