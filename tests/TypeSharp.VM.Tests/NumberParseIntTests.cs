using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class NumberParseIntTests
{
    [Fact]
    public void DirectNumberParseIntUsesGlobalSemantics() => NumberParseIntChecks.CheckOrdinarySource();

    [Fact]
    public void NumberParseIntVmEntryUsesGlobalSemantics() => NumberParseIntChecks.CheckDormantEntry();
}
