using TypeSharp.Hosting;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class GeneratorArtifactIntegrationTests
{
    [Fact]
    public async Task SelectedSourceAndSourceFreeModuleRunTheSameGeneratorCleanup()
    {
        string directory = Path.Combine(Path.GetTempPath(), "typesharp_generator_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "cleanup.ts");
            await File.WriteAllTextAsync(path, """
                let marker:any=31;
                function* gen():any {try {yield 1;} finally {yield marker;}}
                export function main():any {let g:any=gen();g.next();let r:any=g.return?.(7);return r.value;}
                """);
            using var artifact = new MemoryStream();
            await using (var source = await new TypeSharpRuntimeBuilder().AddSourceFile(path).BuildAsync())
            {
                Assert.Equal(31, Assert.IsType<int>(await source.InvokeAsync<object>("cleanup", "main")));
                BytecodeSerializer.Serialize(artifact, source.ActiveGeneration!.Modules["cleanup"].Bytecode);
            }
            File.Delete(path);
            artifact.Position = 0;
            var image = BytecodeSerializer.Deserialize(artifact);
            Assert.Equal(artifact.Length, artifact.Position);
            await using var runtime = await new TypeSharpRuntimeBuilder().BuildAsync();
            Assert.Empty(runtime.ActiveGeneration!.Modules);
            var module = new TsModule("cleanup", path, image, runtime.Modules.NextGenerationId());
            runtime.Modules.Register(path, module);
            runtime.InitializeGeneration();
            Assert.Same(module, runtime.ActiveGeneration!.Modules["cleanup"]);
            Assert.False(File.Exists(path));
            Assert.Equal(31, Assert.IsType<TsInt32Value>(runtime.Invoke("main")).Value);
            Assert.Equal(31, Assert.IsType<int>(await runtime.InvokeAsync<object>("cleanup", "main")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
