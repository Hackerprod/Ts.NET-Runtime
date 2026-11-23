using System;
using System.IO;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class DateTimeUtcInteropIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InvokeAsync_PreservesUtcDateTimeKindAndTicks()
    {
        const string source = "export function identity(value: string): string { return value; }";
        const string expectedText = "2024-02-25T12:15:37.1234567Z";
        const long expectedTicks = 638444601371234567L;

        var input = new DateTime(2024, 2, 25, 12, 15, 37, DateTimeKind.Utc).AddTicks(1_234_567);
        var unspecifiedInput = DateTime.SpecifyKind(input, DateTimeKind.Unspecified);
        Assert.Equal(expectedTicks, input.Ticks);

        TsStringValue raw;
        DateTime actual;
        DateTime unspecified;
        DateTime invalid;
        var dir = Directory.CreateTempSubdirectory("typesharp-datetime-utc-interop-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "app.ts"), source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();

            raw = Assert.IsType<TsStringValue>(await runtime.InvokeAsync<TsValue>("app", "identity", input));
            Assert.Equal(TsValueType.String, raw.ValueType);
            Assert.True(string.Equals(expectedText, raw.Value, StringComparison.Ordinal), "Unexpected round-trip text");

            actual = await runtime.InvokeAsync<DateTime>("app", "identity", input);
            unspecified = await runtime.InvokeAsync<DateTime>("app", "identity", unspecifiedInput);
            invalid = await runtime.InvokeAsync<DateTime>("app", "identity", "not-a-date");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var detail = FormattableString.Invariant($"C189 DateTimeUtcInterop BuildAsync=success; route=app/identity; rawWrapper={raw.GetType().Name}; rawTag={raw.ValueType}; rawText={raw.Value}; expectedKind={DateTimeKind.Utc}; actualKind={actual.Kind}; expectedTicks={expectedTicks}; actualTicks={actual.Ticks}; unspecifiedKind={unspecified.Kind}; unspecifiedTicks={unspecified.Ticks}; fallbackKind={invalid.Kind}; fallbackTicks={invalid.Ticks}");
        output.WriteLine(detail);

        Assert.Equal(DateTimeKind.Unspecified, unspecified.Kind);
        Assert.Equal(unspecifiedInput.Ticks, unspecified.Ticks);
        Assert.Equal(DateTime.MinValue, invalid);
        Assert.Equal(expectedTicks, actual.Ticks);
        Assert.True(actual.Kind == DateTimeKind.Utc, "C189_UTC_DATETIME_EXPECTED_UTC_KIND");
    }
}
