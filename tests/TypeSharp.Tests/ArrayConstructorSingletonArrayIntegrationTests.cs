using System.Globalization;
using System.Text.Json;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class ArrayConstructorSingletonArrayIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ImportedArray_RemainsTheSingleElement()
    {
        const string row = "host-imported-new-dense";
        const string data = "export const input: any[] = [3, 4];";
        const string app = """
            import { input } from "./data";
            export function c105ImportedProbe(): any[] {
                const outer: any[] = new Array<any>(input);
                return [input, outer, JSON.stringify(input)];
            }
            """;
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_c105_import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "data.ts"), data);
        File.WriteAllText(Path.Combine(dir, "app.ts"), app);

        await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
        var generation = Assert.IsType<TypeSharp.Runtime.Generations.RuntimeGeneration>(runtime.ActiveGeneration);
        var modules = generation.Modules.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "app", "data" }, modules);
        var envelope = Assert.IsType<TsArrayValue>(runtime.Invoke("c105ImportedProbe")).Value;
        Assert.Equal(3, envelope.Count);
        for (var i = 0; i < envelope.Count; i++)
            Assert.False(envelope.IsHole(i));
        var input = Assert.IsType<TsArrayValue>(envelope.Get(0));
        var result = Assert.IsType<TsArrayValue>(envelope.Get(1));
        var sourceJson = Assert.IsType<TsStringValue>(envelope.Get(2)).Value;
        Assert.Equal("[3,4]", sourceJson);
        Assert.Equal(2, input.Value.Count);
        var inputSlots = Slots(input.Value);
        Assert.Equal(new[] { "P:Number:3", "P:Number:4" }, inputSlots);
        Assert.NotSame(input, result);
        Assert.NotSame(input.Value, result.Value);
        var first = result.Value.Count != 0 && !result.Value.IsHole(0) ? result.Value.Get(0) as TsArrayValue : null;
        var actual = new Vector(result.Value.Count, Slots(result.Value), ReferenceEquals(input, first), ReferenceEquals(input.Value, first?.Value));
        var expected = new Vector(1, ["P:Array"], true, true);
        var actualJson = JsonSerializer.Serialize(actual);
        var expectedJson = JsonSerializer.Serialize(expected);
        output.WriteLine("C105 native singleton row " + JsonSerializer.Serialize(new
        {
            row, route = "raw-import-host", data, app, modules, envelopeCount = envelope.Count, sourceJson,
            inputCount = input.Value.Count, inputSlots, fresh = true, actual, expected,
        }));
        Assert.True(actualJson == expectedJson, $"C105_ARRAY_SINGLETON_MISMATCH {row} expected={expectedJson} actual={actualJson}");
    }

    private sealed record Vector(int count, string[] slots, bool sameWrapper, bool sameBacking);

    private static string[] Slots(TsArray array) => Enumerable.Range(0, array.Count).Select(i =>
    {
        if (array.IsHole(i)) return "H";
        var value = array.Get(i);
        if (value is TsArrayValue) return "P:Array";
        if (value.RawValue is int or long or ulong or float or double or decimal)
            return "P:Number:" + Convert.ToString(value.RawValue, CultureInfo.InvariantCulture);
        return $"unexpected:{value.ValueType}";
    }).ToArray();
}
