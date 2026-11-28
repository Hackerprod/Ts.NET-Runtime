using TypeSharp.Hosting;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class JsonStringifyHostingWireTextTests
{
    [Fact]
    public async Task HostedJsonPreservesManagedFloatAndStringCarriers()
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-json-wire-");
        try
        {
            var path = Path.Combine(directory.FullName, "entry.ts");
            await File.WriteAllTextAsync(path, "export function main(value: any): string { return JSON.stringify(value); }");
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(path).BuildAsync();
            var module = Assert.Single(runtime.ActiveGeneration!.Modules);
            Assert.Equal(path, module.Value.FilePath);
            BytecodeVerifier.Verify(module.Value.Bytecode);

            Assert.Equal("0.10000000149011612", await runtime.InvokeAsync<string>(module.Key, "main", 0.1f));
            Assert.Equal("\"\\ud800\ud83d\ude00\"", await runtime.InvokeAsync<string>(module.Key, "main", "\ud800\ud83d\ude00"));

            var key = new TsObject("Object");
            key.SetField("\udc00", TsValue.FromString("é"));
            var raw = Assert.IsType<TsStringValue>(runtime.Invoke("main", [TsValue.FromObject(key)]));
            Assert.Equal("{\"\\udc00\":\"é\"}", raw.Value);
            Assert.Same(TsValue.Void, runtime.Invoke("main", [TsValue.Void]));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
