using TypeSharp.Hosting;
using Xunit;

namespace TypeSharp.Tests;

public class OperandStackTests
{
    [Fact]
    public async Task OperandStackGrowsForLargeArrayLiterals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_operand_stack_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "large-array.ts");
        const int elementCount = 20_000;
        var values = string.Join(",", Enumerable.Range(0, elementCount));
        await File.WriteAllTextAsync(file, $$"""
            export function scenario(): number {
                const values = [{{values}}];
                return values.length + values[values.length - 1];
            }
            """);

        try
        {
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .BuildAsync();

            var result = await runtime.InvokeAsync<double>("large-array", "scenario");
            Assert.Equal((elementCount * 2) - 1, result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
