using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class NumberParseIntTests
{
    [Fact]
    public void DirectNumberAndGlobalParseIntKeepDistinctRadixSemantics() => NumberParseIntChecks.CheckOrdinarySource();

    [Fact]
    public void NumberParseIntVmEntryUsesNativeRadixSemantics() => NumberParseIntChecks.CheckDormantEntry();
}
