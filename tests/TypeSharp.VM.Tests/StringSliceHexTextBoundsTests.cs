using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringSliceHexTextBoundsTests(ITestOutputHelper output)
{
    [Fact]
    public void Slice_PreservesExistingBoundsBehavior() =>
        StringSliceHexTextBoundsChecks.CheckControls(line => output.WriteLine(line));

    [Fact]
    public void Slice_CoercesUnsignedHexTextBounds() =>
        StringSliceHexTextBoundsChecks.CheckRegressions(line => output.WriteLine(line));
}
