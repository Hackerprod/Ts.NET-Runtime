using System.Globalization;
using System.Text;
using TypeSharp.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class Uint8ArrayByteLengthIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("elements")]
    [InlineData("empty")]
    [InlineData("sized")]
    public Task ByteLength_ReturnsNativeByteCount(string row) =>
        ObserveAsync(nameof(ByteLength_ReturnsNativeByteCount), row, "byteLength");

    [Theory]
    [InlineData("elements")]
    [InlineData("empty")]
    [InlineData("sized")]
    public Task Length_StillReturnsElementCount(string row) =>
        ObserveAsync(nameof(Length_StillReturnsElementCount), row, "length");

    private async Task ObserveAsync(string method, string row, string member)
    {
        var (source, expected) = row switch
        {
            "elements" => ("new Uint8Array([9, 47, 128])", 3d),
            "empty" => ("new Uint8Array()", 0d),
            "sized" => ("new Uint8Array(5)", 5d),
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };

        var dir = Directory.CreateTempSubdirectory("typesharp-uint8array-byte-length-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir.FullName, "app.ts"),
                "export function observe(): number {\n" +
                $"    const bytes: Uint8Array = {source};\n" +
                $"    return bytes.{member};\n" +
                "}\n",
                new UTF8Encoding(false));

            var tag = $"C145_UINT8_ARRAY method={method} row={row}";
            async Task<TypeSharpRuntime> BuildAsync()
            {
                output.WriteLine($"{tag} stage=hosting-build-start");
                try
                {
                    var built = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir.FullName).BuildAsync();
                    output.WriteLine($"{tag} stage=hosting-build-success");
                    return built;
                }
                catch (Exception ex)
                {
                    output.WriteLine($"{tag} stage=hosting-build-failure type={ex.GetType().FullName}\n{ex}");
                    throw;
                }
            }

            await using var runtime = await BuildAsync();
            var actual = await runtime.InvokeAsync<double>("app", "observe");
            output.WriteLine($"{tag} stage=invoke-success expected={expected.ToString("R", CultureInfo.InvariantCulture)} actual={actual.ToString("R", CultureInfo.InvariantCulture)}");
            Assert.Equal(expected, actual);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
