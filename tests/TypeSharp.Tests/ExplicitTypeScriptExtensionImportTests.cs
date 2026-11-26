using System;
using System.IO;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using Xunit;

namespace TypeSharp.Tests;

public class ExplicitTypeScriptExtensionImportTests
{
    [Fact]
    public async Task MixedCaseExplicitExtensionResolves()
        => await AssertNamedImportAsync("numbers.TS", "./numbers.TS");

    [Fact]
    public async Task LowercaseExplicitExtensionResolves()
        => await AssertNamedImportAsync("numbers.ts", "./numbers.ts");

    [Fact]
    public async Task ExtensionlessSpecifierResolves()
        => await AssertNamedImportAsync("numbers.ts", "./numbers");

    private static async Task AssertNamedImportAsync(string exporterName, string specifier)
    {
        var directory = Path.Combine(Path.GetTempPath(), "typesharp-explicit-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var exporter = Path.Combine(directory, exporterName);
            var reader = Path.Combine(directory, "reader.ts");
            File.WriteAllText(exporter, "export function value(): int32 { return 42; }");
            File.WriteAllText(reader, $$"""
                import { value } from "{{specifier}}";
                export function readValue(): int32 { return value(); }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(exporter)
                .AddSourceFile(reader)
                .BuildAsync();

            Assert.Equal(42, await runtime.InvokeAsync<int>("reader", "readValue"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
