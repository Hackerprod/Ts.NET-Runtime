using System.Globalization;
using System.Numerics;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

[CollectionDefinition(nameof(ConsolePrimitiveFormattingTests), DisableParallelization = true)]
public sealed class ConsolePrimitiveFormattingCollection { }

[Collection(nameof(ConsolePrimitiveFormattingTests))]
public sealed class ConsolePrimitiveFormattingTests
{
    [Fact]
    public void AllMembersFormatBinaryNumbersInvariantlyAndPreserveNegativeZero()
    {
        TsValue[] values =
        [
            TsValue.FromFloat32(0.1f), TsValue.FromFloat64(0.1d),
            TsValue.FromFloat32(0f), TsValue.FromFloat32(BitConverter.Int32BitsToSingle(int.MinValue)),
            TsValue.FromFloat64(0d), TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue)),
            TsValue.FromFloat64(1e-6), TsValue.FromFloat64(1e-7),
            TsValue.FromFloat64(1e20), TsValue.FromFloat64(1e21),
            TsValue.FromFloat32(float.NaN), TsValue.FromFloat64(double.PositiveInfinity),
            TsValue.FromFloat64(double.NegativeInfinity)
        ];

        AssertFamily(values, "0.10000000149011612 0.1 0 -0 0 -0 0.000001 1e-7 100000000000000000000 1e+21 NaN Infinity -Infinity");
        Assert.Equal("0", Assert.IsType<TsStringValue>(Invoke("String", [values[5]])).Value);
    }

    [Fact]
    public void AllMembersKeepExactIntegersAndDecimalSeparateFromBigInt()
    {
        const long exact = 9007199254740993L;
        var bigInt = TsValue.FromBigInt(new BigInteger(exact));
        AssertFamily(
            [TsValue.FromInt32(int.MinValue), TsValue.FromInt64(-exact), TsValue.FromUInt64((ulong)exact),
             TsValue.FromUInt64(ulong.MaxValue), TsValue.FromDecimal(-12345.1000m),
             bigInt, TsValue.Null, TsValue.Void, TsValue.FromBool(true), TsValue.FromBool(false)],
            "-2147483648 -9007199254740993 9007199254740993 18446744073709551615 -12345.1000 9007199254740993n null undefined true false");
        Assert.Equal("9007199254740993", Assert.IsType<TsStringValue>(Invoke("String", [bigInt])).Value);
        Assert.Equal("\"9007199254740993\"", Assert.IsType<TsStringValue>(Invoke("JSON::stringify", [bigInt])).Value);
    }

    [Fact]
    public void AllMembersPreserveLiteralTextFallbackAndWriters()
    {
        var regex = TsValue.FromRegex("a+", "i");
        AssertFamily([TsValue.FromString("%s"), TsValue.FromString("a\r\n🧪"), regex], "%s a\r\n🧪 /a+/i");
        AssertFamily([], "");
        AssertFamily([TsValue.FromString("")], "");
    }

    private static TsValue Invoke(string member, TsValue[] values)
    {
        Assert.True(Builtins.TryGet(member, out var call));
        return call!(values);
    }

    private static void AssertFamily(TsValue[] values, string text)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        var priorOutput = Console.Out;
        var priorError = Console.Error;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (string method in new[] { "log", "error", "warn" })
                {
                    using var stdout = new StringWriter { NewLine = "\n" };
                    using var stderr = new StringWriter { NewLine = "\r\n" };
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    try
                    {
                        Assert.Same(TsValue.Void, Invoke("console::" + method, values));
                        Assert.Equal(method == "log" ? text + "\n" : "", stdout.ToString());
                        Assert.Equal(method == "log" ? "" : text + "\r\n", stderr.ToString());
                    }
                    finally
                    {
                        Console.SetOut(priorOutput);
                        Console.SetError(priorError);
                    }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }
}
