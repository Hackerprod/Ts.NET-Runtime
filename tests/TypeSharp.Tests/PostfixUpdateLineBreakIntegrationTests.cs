using System.Globalization;
using System.Text;
using TypeSharp.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class PostfixUpdateLineBreakIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public Task LineFeed_BeforeIncrement_StartsPrefixStatement() =>
        ObserveAsync(nameof(LineFeed_BeforeIncrement_StartsPrefixStatement), "a = b\n++c;", 447d);

    [Fact]
    public Task LineFeed_BeforeDecrement_StartsPrefixStatement() =>
        ObserveAsync(nameof(LineFeed_BeforeDecrement_StartsPrefixStatement), "a = b\n--c;", 445d);

    [Fact]
    public Task BlockCommentWithLineFeed_BeforeIncrement_StartsPrefixStatement() =>
        ObserveAsync(nameof(BlockCommentWithLineFeed_BeforeIncrement_StartsPrefixStatement), "a = b /* note\n*/ ++c;", 447d);

    [Fact]
    public Task LineFeed_AfterClosingParen_StartsPrefixStatement() =>
        ObserveAsync(nameof(LineFeed_AfterClosingParen_StartsPrefixStatement), "a = (b\n)\n++c;", 447d);

    [Fact]
    public Task SameLineIncrement_RemainsPostfix() =>
        ObserveAsync(nameof(SameLineIncrement_RemainsPostfix), "a = b++;", 456d);

    [Fact]
    public Task SameLineDecrement_RemainsPostfix() =>
        ObserveAsync(nameof(SameLineDecrement_RemainsPostfix), "a = b--;", 436d);

    [Fact]
    public Task SameLineBlockComment_LeavesIncrementPostfix() =>
        ObserveAsync(nameof(SameLineBlockComment_LeavesIncrementPostfix), "a = b /* note */ ++;", 456d);

    [Fact]
    public Task SameLineClosingParen_LeavesIncrementPostfix() =>
        ObserveAsync(nameof(SameLineClosingParen_LeavesIncrementPostfix), "a = (b\n)++;", 456d);

    [Fact]
    public Task ExistingPrefixIncrement_ReturnsNewValue() =>
        ObserveAsync(nameof(ExistingPrefixIncrement_ReturnsNewValue), "a = ++b;", 556d);

    [Fact]
    public Task ExplicitSemicolon_AllowsFollowingPrefixIncrement() =>
        ObserveAsync(nameof(ExplicitSemicolon_AllowsFollowingPrefixIncrement), "a = b;\n++c;", 447d);

    private async Task ObserveAsync(string row, string assignment, double expected)
    {
        var dir = Directory.CreateTempSubdirectory("typesharp-postfix-lf-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir.FullName, "app.ts"),
                "export function observe(): number {\n" +
                "    let a: number = 1;\n    let b: number = 4;\n    let c: number = 6;\n    " +
                assignment + "\n    return a * 100 + b * 10 + c;\n}\n",
                new UTF8Encoding(false));

            output.WriteLine($"C144_POSTFIX_LF_STAGE row={row} stage=hosting-build-start");
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir.FullName).BuildAsync();
            output.WriteLine($"C144_POSTFIX_LF_STAGE row={row} stage=hosting-build-success");
            var actual = await runtime.InvokeAsync<double>("app", "observe");
            output.WriteLine($"C144_POSTFIX_LF_STAGE row={row} stage=hosting-invoke-success");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"C144_POSTFIX_LF_VALUE row={row} expected={expected:R} actual={actual:R}"));
            Assert.Equal(expected, actual);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
