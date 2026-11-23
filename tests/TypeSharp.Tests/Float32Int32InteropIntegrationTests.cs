using System;
using System.IO;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class Float32Int32InteropIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InvokeAsync_TruncatesInRangeFloat32ToInt32()
    {
        const string source = "export function identity(value: any): any { return value; }";
        const string module = "app";
        const string export = "identity";
        const float input = -7.75f;
        const int expectedInt = -7;

        var dir = Directory.CreateTempSubdirectory("typesharp-float32-int32-interop-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, module + ".ts"), source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();

            var raw = Assert.IsType<TsFloat32Value>(await runtime.InvokeAsync<TsValue>(module, export, input));
            Assert.Equal(TsValueType.Float32, raw.ValueType);
            Assert.Equal(-7.75f, raw.Value);

            var floatResult = await runtime.InvokeAsync<float>(module, export, input);
            var float64Int = await runtime.InvokeAsync<int>(module, export, -7.75d);
            var int32Int = await runtime.InvokeAsync<int>(module, export, 9);
            var stringInt = await runtime.InvokeAsync<int>(module, export, "not numeric");
            Assert.Equal(-7.75f, floatResult);
            Assert.Equal(-7, float64Int);
            Assert.Equal(9, int32Int);
            Assert.Equal(0, stringInt);

            var overflowInt = await runtime.InvokeAsync<int>(module, export, 2147483648f);
            var underflowInt = await runtime.InvokeAsync<int>(module, export, -2147483904f);
            var nanInt = await runtime.InvokeAsync<int>(module, export, float.NaN);
            var posInfinityInt = await runtime.InvokeAsync<int>(module, export, float.PositiveInfinity);
            var negInfinityInt = await runtime.InvokeAsync<int>(module, export, float.NegativeInfinity);
            Assert.Equal(0, overflowInt);
            Assert.Equal(0, underflowInt);
            Assert.Equal(0, nanInt);
            Assert.Equal(0, posInfinityInt);
            Assert.Equal(0, negInfinityInt);

            var actualInt = await runtime.InvokeAsync<int>(module, export, input);
            output.WriteLine(FormattableString.Invariant(
                $"C190 Float32Int32Interop BuildAsync=success; route={module}/{export}; rawWrapper={raw.GetType().Name}; rawTag={raw.ValueType}; rawValue={raw.Value}; floatResult={floatResult}; float64Int={float64Int}; int32Int={int32Int}; stringInt={stringInt}; overflowInt={overflowInt}; underflowInt={underflowInt}; nanInt={nanInt}; posInfinityInt={posInfinityInt}; negInfinityInt={negInfinityInt}; expectedInt={expectedInt}; actualInt={actualInt}"));
            Assert.True(actualInt == expectedInt, "C190_FLOAT32_EXPECTED_INT32_TRUNCATION");

            var nullablePositiveInt = await runtime.InvokeAsync<int?>(module, export, 7.75f);
            var minInt = await runtime.InvokeAsync<int>(module, export, -2147483648f);
            var maxFloat32Int = await runtime.InvokeAsync<int>(module, export, 2147483520f);
            Assert.Equal<int?>(7, nullablePositiveInt);
            Assert.Equal(int.MinValue, minInt);
            Assert.Equal(2147483520, maxFloat32Int);
            output.WriteLine(FormattableString.Invariant(
                $"C190 Float32Int32Interop accepted; nullablePositiveInt={nullablePositiveInt}; minInt={minInt}; maxFloat32Int={maxFloat32Int}"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
