using System.Numerics;
using TypeSharp.Hosting;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class BigIntArtifactIntegrationTests
{
    [Fact]
    public async Task SourceFreeRegisteredArtifactConvertsManagedInputsAtPublicEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typesharp_bigint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "convert.ts");
            var artifact = Path.Combine(directory, "convert.tsbc");
            await File.WriteAllTextAsync(source, """
                export function main(input: any): any {
                    const convert: any = BigInt;
                    try { return convert?.(input); } catch (e: any) { return e.name; }
                }
                """);
            await using (var builderRuntime = await new TypeSharpRuntimeBuilder().AddSourceFile(source).BuildAsync())
            {
                using var output = File.Create(artifact);
                BytecodeSerializer.Serialize(output, builderRuntime.ActiveGeneration!.Modules["convert"].Bytecode);
            }

            File.Delete(source);
            BytecodeModule restored;
            using (var input = File.OpenRead(artifact))
            {
                restored = BytecodeSerializer.Deserialize(input);
                BytecodeVerifier.Verify(restored);
                Assert.Equal(input.Length, input.Position);
            }
            await using var runtime = await new TypeSharpRuntimeBuilder().BuildAsync();
            Assert.Empty(runtime.ActiveGeneration!.Modules);
            var dependency = new TsModule("convert", source, restored, runtime.Modules.NextGenerationId());
            runtime.Modules.Register(source, dependency);
            runtime.InitializeGeneration();
            Assert.Same(dependency, runtime.ActiveGeneration!.Modules["convert"]);
            Assert.False(File.Exists(source));

            Assert.Equal(new BigInteger(9007199254740993L),
                Assert.IsType<BigInteger>(await runtime.InvokeAsync<object>("convert", "main", 9007199254740993m)));
            Assert.Equal("RangeError", Assert.IsType<string>(await runtime.InvokeAsync<object>("convert", "main", 1e-28m)));
            Assert.Equal("SyntaxError", Assert.IsType<string>(await runtime.InvokeAsync<object>("convert", "main", "12\0")));
            Assert.Equal("TypeError", Assert.IsType<string>(await runtime.InvokeAsync<object>("convert", "main", (object)null!)));
            Assert.Equal(new BigInteger(255),
                Assert.IsType<TsBigIntValue>(runtime.Invoke("main", [TsValue.FromString("0xFF")])).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
