using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerQuotedLineContinuationTests
{
    [Fact]
    public void QuotedLfAndCrLfContinuationsPreserveCookingAndLocations() =>
        LexerQuotedLineContinuationChecks.Control();

    [Fact]
    public void QuotedCrLsAndPsContinuationsPreserveCookingAndLocations() =>
        LexerQuotedLineContinuationChecks.Regression();
}
