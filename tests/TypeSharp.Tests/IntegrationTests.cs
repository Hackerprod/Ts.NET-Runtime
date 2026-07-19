using TypeSharp.Hosting;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Hosting.HotReload;
using TypeSharp.Runtime.Generations;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task EndToEndFunction()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("samples/basic/main", "add", 3, 4);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task EndToEndFactorial()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("samples/basic/main", "factorial", 5);
        Assert.Equal(120, result);
    }

    [Fact]
    public async Task EndToEndFibonacci()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("samples/basic/main", "fibonacci", 10);
        Assert.Equal(55, result);
    }

    [Fact]
    public async Task EndToEndGreet()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<string>("samples/basic/main", "greet", "World");
        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public async Task EndToEndIsEven()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        Assert.True(await runtime.InvokeAsync<bool>("samples/basic/main", "isEven", 4));
        Assert.False(await runtime.InvokeAsync<bool>("samples/basic/main", "isEven", 7));
    }

    [Fact]
    public async Task HostServiceRegistration()
    {
        var builder = new TypeSharpRuntimeBuilder()
            .AddHostService("math", new MathHostService());
        await using var runtime = await builder.BuildAsync();

        Assert.NotNull(runtime);
    }

    [Fact]
    public async Task HostNumber_CanBeComparedWithModuleConstant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_host_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                const expected = 4006;

                function matches(): boolean {
                    return messageType() == expected;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .RegisterHostFunction("gc", "messageType", _ => TsValue.FromInt32(4006))
                .BuildAsync();

            var result = runtime.Invoke("matches");
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result!).Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HostNumber_CanBeComparedWithNumericLiteral()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_host_literal_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                function matches(): boolean {
                    return messageType() == 4006;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .RegisterHostFunction("gc", "messageType", _ => TsValue.FromInt32(4006))
                .BuildAsync();

            var result = runtime.Invoke("matches");
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result!).Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HotReload_PublishesValidatedCandidateAndRollsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_reload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .BuildAsync();

            Assert.Equal(1, ((TsInt32Value)runtime.Invoke("value")!).Value);
            var initial = runtime.ActiveGeneration;

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.True(await runtime.ReloadAsync(file));
            Assert.Equal(2, ((TsInt32Value)runtime.Invoke("value")!).Value);
            Assert.NotSame(initial, runtime.ActiveGeneration);
            Assert.Contains(initial!, runtime.RetiredGenerations);

            File.WriteAllText(file, "function value(): string { return 3; }");
            Assert.False(await runtime.ReloadAsync(file));
            Assert.Equal(2, ((TsInt32Value)runtime.Invoke("value")!).Value);

            Assert.True(await runtime.RollbackAsync());
            Assert.Equal(1, ((TsInt32Value)runtime.Invoke("value")!).Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_CanaryRejectsCandidateWithoutPublishing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_canary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .ConfigureHotReload(options => options.Canary = _ => false)
                .BuildAsync();

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.False(await runtime.ReloadAsync(file));
            Assert.Equal(1, ((TsInt32Value)runtime.Invoke("value")!).Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_GenerationLeaseTracksActiveExecutions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_lease_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .BuildAsync();

            var generation = runtime.ActiveGeneration!;
            using (runtime.AcquireGeneration())
                Assert.Equal(1, generation.ActiveExecutions);
            Assert.Equal(0, generation.ActiveExecutions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_RunsExplicitMigratorBeforePublish()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            var migrator = new TrackingMigrator();
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .ConfigureHotReload(options => options.Migrators.Add(migrator))
                .BuildAsync();

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.True(await runtime.ReloadAsync(file));
            Assert.True(migrator.WasMigrated);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_RetainsLeasedGenerationUntilExecutionCompletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_retain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .ConfigureHotReload(options => options.RetainedGenerations = 0)
                .BuildAsync();

            using (runtime.AcquireGeneration())
            {
                File.WriteAllText(file, "function value(): int32 { return 2; }");
                Assert.True(await runtime.ReloadAsync(file));
                Assert.Single(runtime.RetiredGenerations);
            }

            Assert.Empty(runtime.RetiredGenerations);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class TrackingMigrator : IGenerationMigrator
    {
        public bool WasMigrated { get; private set; }
        public bool CanMigrate(RuntimeGeneration previous, RuntimeGeneration candidate) => true;
        public void Migrate(RuntimeGeneration previous, RuntimeGeneration candidate) => WasMigrated = true;
    }
}

public class ModuleNamingTests
{
    [Fact]
    public void ComputeModuleId_TopLevelFile()
    {
        var id = TypeScriptCompilation.ComputeModuleId("/project/users.ts", "/project");
        Assert.Equal("users", id);
    }

    [Fact]
    public void ComputeModuleId_NestedFile()
    {
        var id = TypeScriptCompilation.ComputeModuleId("/project/services/users.ts", "/project");
        Assert.Equal("services/users", id);
    }

    [Fact]
    public void ComputeModuleId_IndexFile()
    {
        var id = TypeScriptCompilation.ComputeModuleId("/project/services/users/index.ts", "/project");
        Assert.Equal("services/users/index", id);
    }

    [Fact]
    public void ComputeModuleId_NoCollision()
    {
        var id1 = TypeScriptCompilation.ComputeModuleId("/project/scripts/a.ts", "/project");
        var id2 = TypeScriptCompilation.ComputeModuleId("/project/scripts/b.ts", "/project");
        Assert.NotEqual(id1, id2);
        Assert.Equal("scripts/a", id1);
        Assert.Equal("scripts/b", id2);
    }
}

public class CompilationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Compilation_ParsesMultipleFiles()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.ts"), "function add(a: int32, b: int32): int32 { return a + b; }");
            File.WriteAllText(Path.Combine(dir, "b.ts"), "function main(): int32 { return 42; }");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors);
            Assert.Equal(2, comp.CompiledModules.Count);
            Assert.Contains("a", comp.CompiledModules.Keys);
            Assert.Contains("b", comp.CompiledModules.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_CanonicalModuleIds()
    {
        var dir = TempDir();
        try
        {
            var sub = Path.Combine(dir, "services");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "users.ts"), "function main(): int32 { return 1; }");
            File.WriteAllText(Path.Combine(sub, "orders.ts"), "function main(): int32 { return 2; }");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            comp.Compile();

            Assert.Contains("services/users", comp.CompiledModules.Keys);
            Assert.Contains("services/orders", comp.CompiledModules.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassesModule()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Counter {
    count: int32;
    constructor(initial: int32) {
        this.count = initial;
    }
    getCount(): int32 {
        return this.count;
    }
}
function main(): int32 {
    const c = new Counter(10);
    return c.getCount();
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            var modules = comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors);
            Assert.Single(modules);
            Assert.Contains("Counter::.ctor", modules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Counter::getCount", modules["app"].Bytecode.FunctionIndex);
            Assert.Contains("main", modules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ImportBinding_CrossModule()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "math.ts"), @"
export function add(a: int32, b: int32): int32 {
    return a + b;
}
export function multiply(a: int32, b: int32): int32 {
    return a * b;
}
");
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
import { add, multiply } from './math';

function main(): int32 {
    const x = add(3, 4);
    const y = multiply(5, 6);
    return x + y;
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "math.ts"));
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            var errors = comp.Diagnostics.GetErrors().ToList();
            Assert.False(comp.Diagnostics.HasErrors, "Errors: " + string.Join("; ", errors.Select(e => e.Message)));
            Assert.Equal(2, comp.CompiledModules.Count);
            Assert.Contains("math", comp.CompiledModules.Keys);
            Assert.Contains("app", comp.CompiledModules.Keys);
            Assert.Contains("add", comp.CompiledModules["math"].Bytecode.FunctionIndex);
            Assert.Contains("multiply", comp.CompiledModules["math"].Bytecode.FunctionIndex);
            Assert.Contains("main", comp.CompiledModules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassMethodCanCallLaterMethod()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "worker.ts"), @"
export class Worker {
    run(value: int32): boolean {
        return this.isExpected(value);
    }

    isExpected(value: int32): boolean {
        return value == 42;
    }
}
");
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
import { Worker } from './worker';

function main(): boolean {
    const worker = new Worker();
    return worker.run(42);
}
");

            var runtime = new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .BuildAsync()
                .GetAwaiter()
                .GetResult();

            var result = runtime.Invoke("main");

            Assert.NotNull(result);
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result).Value);

            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ImportBinding_SecondModule()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "utils.ts"), @"
export function greet(name: string): string {
    return 'Hello ' + name;
}
");
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
import { greet } from './utils';

function main(): string {
    return greet('TypeSharp');
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "utils.ts"));
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            var errors = comp.Diagnostics.GetErrors().ToList();
            Assert.False(comp.Diagnostics.HasErrors, "Errors: " + string.Join("; ", errors.Select(e => e.Message)));
            Assert.Contains("greet", comp.CompiledModules["utils"].Bytecode.FunctionIndex);
            Assert.Contains("main", comp.CompiledModules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassHierarchy_BaseConstructor()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Animal {
    name: string;
    constructor(name: string) {
        this.name = name;
    }
    speak(): string {
        return this.name + ' makes a noise';
    }
}

class Dog extends Animal {
    breed: string;
    constructor(name: string, breed: string) {
        super(name);
        this.breed = breed;
    }
    speak(): string {
        return this.name + ' barks';
    }
}

function main(): string {
    const d = new Dog('Rex', 'Labrador');
    return d.speak();
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors, "Errors: " + string.Join("; ", comp.Diagnostics.GetErrors().Select(e => e.Message)));
            Assert.Contains("Animal::.ctor", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Dog::.ctor", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Animal::speak", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Dog::speak", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("main", comp.CompiledModules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassHierarchy_RejectsIncompatibleOverride()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Base {
    describe(value: int32): string { return 'base'; }
}
class Derived extends Base {
    describe(value: string): int32 { return 1; }
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            Assert.Contains(comp.Diagnostics.GetErrors(), d => d.Code == TypeSharp.Syntax.Diagnostics.DiagnosticCode.TS2013);
            Assert.Contains(comp.Diagnostics.GetErrors(), d => d.Code == TypeSharp.Syntax.Diagnostics.DiagnosticCode.TS2015);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassHierarchy_RejectsSuperOutsideConstructor()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Base { }
class Derived extends Base {
    initialize(): void { super(); }
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            Assert.Contains(comp.Diagnostics.GetErrors(), d => d.Code == TypeSharp.Syntax.Diagnostics.DiagnosticCode.TS2001);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class MathHostService
{
    public int Add(int a, int b) => a + b;
    public int Multiply(int a, int b) => a * b;
}
