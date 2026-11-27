using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySpliceHexTextBoundsTests(ITestOutputHelper output)
{
    [Fact]
    public void ArraySplice_HexTextBounds() =>
        ArraySpliceHexTextBoundsChecks.CheckRegressions(output.WriteLine);

    [Fact]
    public void ArraySplice_LegacyBoundsAndOmission() =>
        ArraySpliceHexTextBoundsChecks.CheckControls(output.WriteLine);
}
