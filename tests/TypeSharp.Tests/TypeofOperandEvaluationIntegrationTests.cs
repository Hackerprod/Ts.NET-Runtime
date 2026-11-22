using System.Globalization;
using System.Numerics;
using TypeSharp.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class TypeofOperandEvaluationIntegrationTests(ITestOutputHelper output)
{
    private const string Module = "typeof-operand-evaluation";
    private const string Guest = """
        export function known(): string {
            let count: int32 = 0;
            let tag: string = typeof (count = count + 1);
            return tag + ":" + count;
        }

        function choose(value: any, ignored: int32): any { return value; }
        export function dynamicTag(value: any): string {
            let count: int32 = 0;
            let tag: string = typeof choose(value, (count = count + 1));
            return tag + ":" + count;
        }

        export function pure(wide: int64, text: string, flag: bool, big: bigint): string {
            return (typeof wide) + ":" + (typeof text) + ":" + (typeof flag) + ":" + (typeof big);
        }
        """;

    [Fact]
    public async Task KnownNumericOperand_EvaluatesOnceAndKeepsTag()
    {
        var result = await InvokeAsync("known", "known");
        Assert.NotNull(result);
        var fields = result.Split(':');
        Assert.Equal(2, fields.Length);
        Assert.Equal("number", fields[0]);
        Assert.True(int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count));
        Assert.Equal(count.ToString(CultureInfo.InvariantCulture), fields[1]);
        output.WriteLine($"C129 known observed tag={fields[0]} count={count} result={result}");
        Assert.True(count == 1, $"C129_KNOWN_TYPEOF_OPERAND_COUNT_MISMATCH expected=1 actual={count} result={result}");
    }

    [Fact]
    public async Task DynamicOperand_EvaluatesOnceAndKeepsRuntimeTag()
    {
        var text = await InvokeAsync("dynamic-string", "dynamicTag", "sample");
        var flag = await InvokeAsync("dynamic-boolean", "dynamicTag", true);
        Assert.Equal("string:1", text);
        Assert.Equal("boolean:1", flag);
    }

    [Fact]
    public async Task PureKnownOperands_KeepExistingPrimitiveTags()
    {
        var result = await InvokeAsync("pure", "pure", 7L, "sample", true, new BigInteger(7));
        Assert.Equal("number:string:boolean:bigint", result);
    }

    private async Task<string> InvokeAsync(string row, string export, params object[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp-c129-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, Module + ".ts");
            File.WriteAllText(file, Guest);
            output.WriteLine($"C129 row={row} guest source:");
            output.WriteLine(Guest);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(file).BuildAsync();
            output.WriteLine($"C129 row={row} Hosting AddSourceFile/BuildAsync succeeded");
            var result = await runtime.InvokeAsync<string>(Module, export, args);
            output.WriteLine($"C129 row={row} Hosting InvokeAsync<string> succeeded result={result}");
            return result;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
