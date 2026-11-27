using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class LexerConnectorIdentifierTests
{
    [Fact]
    public void ExistingIdentifierBoundariesRemainUnchanged() => LexerConnectorIdentifierChecks.Control();

    [Fact]
    public void VisibleConnectorPunctuationContinuesIdentifiers() => LexerConnectorIdentifierChecks.Regression();
}
