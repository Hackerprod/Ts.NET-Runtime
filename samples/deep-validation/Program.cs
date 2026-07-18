
using System.Collections.Concurrent;
using TypeSharp.Hosting;
using TypeSharp.Hosting.HotReload;
using TypeSharp.Interop.HostExports;
using TypeSharp.Runtime.Generations;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;

internal enum ValidationArea
{
    Semantics,
    Numeric,
    ObjectModel,
    Modules,
    TypeSafety,
    HostInterop,
    Bytecode,
    HotReload,
    Isolation,
    Limits,
}

internal sealed record ValidationResult(
    ValidationArea Area,
    string Name,
    bool Passed,
    string Details,
    TimeSpan Duration);

internal static class Program
{
    private static readonly List<ValidationResult> Results = new();
    private static string ScriptsRoot = string.Empty;

    public static async Task<int> Main()
    {
        ScriptsRoot = ResolveScriptsRoot();

        Console.WriteLine("TypeSharp Deep Validation — Round 2");
        Console.WriteLine(new string('=', 84));
        Console.WriteLine($"Scripts: {ScriptsRoot}");
        Console.WriteLine("This suite deliberately probes semantic and generation edge cases.");
        Console.WriteLine();

        await RunStableSemantics();
        await RunFeatureSemantics();
        await RunNumericTests();
        await RunObjectModelTests();
        await RunModuleTests();
        await RunTypeSafetyTests();
        await RunHostInteropTests();
        await RunBytecodeTests();
        await RunHotReloadTests();
        await RunIsolationAndLimitTests();

        PrintSummary();
        return Results.All(result => result.Passed) ? 0 : 1;
    }

    private static async Task RunStableSemantics()
    {
        await using var runtime = await BuildSingleFileRuntime(Script("stable", "main.ts"));

        await Expect(runtime, ValidationArea.Semantics, "operator precedence", "stable", "precedence", 9);
        await Expect(runtime, ValidationArea.Semantics, "parenthesized precedence", "stable", "groupedPrecedence", 15);
        await Expect(runtime, ValidationArea.Semantics, "left associativity", "stable", "leftAssociative", 25);
        await Expect(runtime, ValidationArea.Semantics, "for-loop sum", "stable", "forSum", 5_050, 100);
        await Expect(runtime, ValidationArea.Semantics, "nested for-loops", "stable", "nestedLoops", 138, 3, 4);
        await Expect(runtime, ValidationArea.Semantics, "forward function reference", "stable", "forwardReference", 42, 8);
        await Expect(runtime, ValidationArea.Semantics, "mutual recursion even", "stable", "isEven", true, 50);
        await Expect(runtime, ValidationArea.Semantics, "mutual recursion odd", "stable", "isOdd", true, 51);
        await Expect(runtime, ValidationArea.Semantics, "early return negative", "stable", "earlyReturn", -1, -10);
        await Expect(runtime, ValidationArea.Semantics, "early return zero", "stable", "earlyReturn", 0, 0);
        await Expect(runtime, ValidationArea.Semantics, "early return positive", "stable", "earlyReturn", 18, 9);
        await Expect(runtime, ValidationArea.Semantics, "branch merge true path", "stable", "branchMerge", 34, 10);
        await Expect(runtime, ValidationArea.Semantics, "branch merge false path", "stable", "branchMerge", 14, 2);
        await Expect(runtime, ValidationArea.Semantics, "logical precedence false", "stable", "logicalPrecedence", false, false, true, false);
        await Expect(runtime, ValidationArea.Semantics, "logical precedence true", "stable", "logicalPrecedence", true, false, true, true);
        await Expect(runtime, ValidationArea.Semantics, "recursive sum", "stable", "recursiveSum", 5_050, 100);
    }

    private static async Task RunFeatureSemantics()
    {
        await RunScriptProbe<int>(ValidationArea.Semantics, "conditional expression true",
            Script("features", "ternary.ts"), "ternary", "choose", 11, true, 11, 22);
        await RunScriptProbe<int>(ValidationArea.Semantics, "conditional expression false",
            Script("features", "ternary.ts"), "ternary", "choose", 22, false, 11, 22);
        await RunScriptProbe<int>(ValidationArea.Semantics, "compound assignment operators",
            Script("features", "compound.ts"), "compound", "compound", 16, 7);
        await RunScriptProbe<int>(ValidationArea.Semantics, "prefix and postfix increment",
            Script("features", "increment.ts"), "increment", "increments", 507, 5);
        await RunScriptProbe<int>(ValidationArea.Semantics, "array literal and indexed access",
            Script("features", "arrays.ts"), "arrays", "arrayScenario", 479);
        await RunScriptProbe<int>(ValidationArea.Semantics, "try/finally normal path",
            Script("features", "exceptions.ts"), "exceptions", "exceptionScenario", 12, 5);
        await RunScriptProbe<int>(ValidationArea.Semantics, "throw/catch/finally path",
            Script("features", "exceptions.ts"), "exceptions", "exceptionScenario", 42, -1);

        await Run(ValidationArea.Semantics, "nullable optional access returns null", async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(Script("features", "nullable.ts"));
            int? actual = await runtime.InvokeAsync<int?>("nullable", "nullableNull", new object[] { null! });
            if (actual != null)
                throw new InvalidOperationException($"Expected null, got {actual}");
            return "null";
        });

        await RunScriptProbe<int?>(ValidationArea.Semantics, "nullable optional access returns value",
            Script("features", "nullable.ts"), "nullable", "nullableValue", 8);
    }

    private static async Task RunNumericTests()
    {
        await using var runtime = await BuildSingleFileRuntime(Script("numeric", "main.ts"));

        await Expect(runtime, ValidationArea.Numeric, "int64 comparison less", "numeric", "int64Compare", 11, 5L, 8L);
        await Expect(runtime, ValidationArea.Numeric, "int64 comparison equal", "numeric", "int64Compare", 38, 8L, 8L);
        await Expect(runtime, ValidationArea.Numeric, "int64 comparison greater", "numeric", "int64Compare", 56, 9L, 8L);

        const ulong low = 9_223_372_036_854_775_900UL;
        const ulong high = 18_446_744_073_709_551_000UL;
        await Expect(runtime, ValidationArea.Numeric, "uint64 comparison less", "numeric", "uint64Compare", 11, low, high);
        await Expect(runtime, ValidationArea.Numeric, "uint64 comparison equal", "numeric", "uint64Compare", 38, high, high);
        await Expect(runtime, ValidationArea.Numeric, "uint64 comparison greater", "numeric", "uint64Compare", 56, high, low);

        const ulong a = 4_000_000_000_000_000_000UL;
        const ulong b = 1_000_000_000_000_000_000UL;
        ulong expectedPipeline = ((a + b) * 3UL - b) / 2UL;
        await Expect(runtime, ValidationArea.Numeric, "uint64 arithmetic pipeline",
            "numeric", "uint64Pipeline", expectedPipeline, a, b);
        await Expect(runtime, ValidationArea.Numeric, "uint64 remainder",
            "numeric", "uint64Remainder", 17UL, 1_000_000_000_000_000_017UL, 1_000_000_000_000_000_000UL);

        long bitA = 0x1234_5678_1122_3344L;
        long bitB = 0x00FF_00FF_00FF_00FFL;
        long bitExpected = ((bitA ^ bitB) | (bitA << 4)) & long.MaxValue;
        await Expect(runtime, ValidationArea.Numeric, "int64 bitwise and shifts",
            "numeric", "int64Bitwise", bitExpected, bitA, bitB);

        await ExpectFloat(runtime, ValidationArea.Numeric, "float32 arithmetic",
            "numeric", "float32Pipeline", (3.5f * 2.0f + 3.5f) / 2.0f, 3.5f, 2.0f);

        decimal decA = 12.50m;
        decimal decB = 0.25m;
        decimal decC = 3m;
        decimal decExpected = (decA + decB) * decC - decA / decC;
        await Expect(runtime, ValidationArea.Numeric, "decimal exact arithmetic",
            "numeric", "decimalPipeline", decExpected, decA, decB, decC);
        await Expect(runtime, ValidationArea.Numeric, "decimal comparison less",
            "numeric", "decimalCompare", -1, 1.1m, 1.2m);
        await Expect(runtime, ValidationArea.Numeric, "decimal comparison equal",
            "numeric", "decimalCompare", 0, 1.2m, 1.2m);
        await Expect(runtime, ValidationArea.Numeric, "decimal comparison greater",
            "numeric", "decimalCompare", 1, 1.3m, 1.2m);

        await Expect(runtime, ValidationArea.Numeric, "negative remainder semantics",
            "numeric", "signedRemainder", -2, -17, 5);

        await Run(ValidationArea.Numeric, "division by zero propagates", async () =>
        {
            try
            {
                _ = await runtime.InvokeAsync<int>("numeric", "divideByZero", 10);
                throw new InvalidOperationException("Division by zero completed successfully");
            }
            catch (Exception ex) when (!ex.Message.Contains("completed successfully", StringComparison.Ordinal))
            {
                return $"{ex.GetType().Name}: {SingleLine(ex.Message)}";
            }
        });
    }

    private static async Task RunObjectModelTests()
    {
        await RunScriptProbe<int>(ValidationArea.ObjectModel, "two instances keep isolated fields",
            Script("objects", "basic.ts"), "basic", "instanceIsolation", 15_107);
        await RunScriptProbe<int>(ValidationArea.ObjectModel, "constructor argument ordering",
            Script("objects", "basic.ts"), "basic", "constructorOrder", 1_203, 12, 3);
        await RunScriptProbe<int>(ValidationArea.ObjectModel, "inheritance, super and inherited method",
            Script("objects", "inheritance.ts"), "inheritance", "inheritanceScenario", 1_316);
        await RunScriptProbe<int>(ValidationArea.ObjectModel, "nested structural object access",
            Script("objects", "structural.ts"), "structural", "nestedStructural", 809_701);
        await RunScriptProbe<int>(ValidationArea.ObjectModel, "interface property mutation",
            Script("objects", "structural.ts"), "structural", "propertyMutation", 119);
    }

    private static async Task RunModuleTests()
    {
        await RunDirectoryProbe(ValidationArea.Modules, "three-level import graph",
            Script("modules", "deep"), "app", "deepScenario", 25);
        await RunDirectoryProbe(ValidationArea.Modules, "aliased import",
            Script("modules", "alias"), "app", "aliasScenario", 83);
        await RunDirectoryProbe(ValidationArea.Modules, "same basename in separate directories",
            Script("modules", "collision"), "app", "collisionScenario", 1_129);
        await RunDirectoryProbe(ValidationArea.Modules, "imported class construction",
            Script("modules", "classes"), "app", "importedClassScenario", 83);

        await Run(ValidationArea.Modules, "canonical module IDs remain distinct", async () =>
        {
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(Script("modules", "collision"))
                .BuildAsync();

            string[] expected = { "left/shared", "right/shared", "app" };
            foreach (string id in expected)
            {
                if (!runtime.ActiveGeneration!.Modules.ContainsKey(id))
                    throw new InvalidOperationException($"Missing canonical module ID '{id}'");
            }
            return string.Join(", ", expected);
        });

        await ExpectDirectoryCompileRejected("circular dependency is rejected",
            Script("modules", "circular"));
        await ExpectDirectoryCompileRejected("missing imported module is rejected",
            Script("modules", "missing"));
    }

    private static async Task RunTypeSafetyTests()
    {
        await ExpectCompileRejected("const reassignment", Script("negative", "const-reassign.ts"));
        await ExpectCompileRejected("wrong return type", Script("negative", "return-type.ts"));
        await ExpectCompileRejected("wrong argument count", Script("negative", "argument-count.ts"));
        await ExpectCompileRejected("wrong argument type", Script("negative", "argument-type.ts"));
        await ExpectCompileRejected("non-bool condition", Script("negative", "non-bool-condition.ts"));
        await ExpectCompileRejected("unknown interface member", Script("negative", "unknown-member.ts"));
        await ExpectCompileRejected("constructor argument type", Script("negative", "constructor-type.ts"));
        await ExpectCompileRejected("missing required interface property", Script("negative", "missing-interface-property.ts"));
    }

    private static async Task RunHostInteropTests()
    {
        var service = new TypedHostService();
        await using var runtime = await new TypeSharpRuntimeBuilder()
            .AddHostService("typed", service)
            .AddSourceFile(Script("host", "main.ts"))
            .BuildAsync();

        await Expect(runtime, ValidationArea.HostInterop, "typed host int32",
            "host", "hostInt", 17, 10);
        await Expect(runtime, ValidationArea.HostInterop, "typed host int64",
            "host", "hostInt64", 3_000_000_000_007L, 3_000_000_000_000L, 7L);
        await Expect(runtime, ValidationArea.HostInterop, "typed host uint64",
            "host", "hostUInt64", ulong.MaxValue - 11, ulong.MaxValue - 11);
        await Expect(runtime, ValidationArea.HostInterop, "typed host decimal",
            "host", "hostDecimal", 12.75m, 12.50m, 0.25m);
        await ExpectFloat(runtime, ValidationArea.HostInterop, "typed host float32",
            "host", "hostFloat", 7.5f, 3.0f);
        await Expect(runtime, ValidationArea.HostInterop, "typed host bool",
            "host", "hostBool", false, true);
        await Expect(runtime, ValidationArea.HostInterop, "typed host string",
            "host", "hostString", "[alpha]", "alpha");
        await Expect(runtime, ValidationArea.HostInterop, "Task<T> host method",
            "host", "hostTask", 42, 21);
        await Expect(runtime, ValidationArea.HostInterop, "ValueTask<T> host method",
            "host", "hostValueTask", 9_000_000_001L, 9_000_000_000L);

        await Run(ValidationArea.HostInterop, "host exception propagates", async () =>
        {
            try
            {
                _ = await runtime.InvokeAsync<int>("host", "hostFailure", 5);
                throw new InvalidOperationException("Host exception was swallowed");
            }
            catch (Exception ex) when (!ex.Message.Contains("swallowed", StringComparison.Ordinal))
            {
                return $"{ex.GetType().Name}: {SingleLine(ex.Message)}";
            }
        });

        await Run(ValidationArea.HostInterop, "dynamic host return preserves int64 arithmetic", async () =>
        {
            await using var dynamicRuntime = await new TypeSharpRuntimeBuilder()
                .RegisterHostFunction("dynamic", "dynamicLong", args =>
                {
                    long value = ((TsInt64Value)args[0]).Value;
                    return TsValue.FromInt64(value + 5_000_000_000L);
                })
                .AddSourceFile(CreateTemporaryScript(
                    "export function scenario(value: int64): int64 { return dynamicLong(value) + 7; }"))
                .BuildAsync();

            string module = GetOnlyModuleName(dynamicRuntime);
            long actual = await dynamicRuntime.InvokeAsync<long>(module, "scenario", 9L);
            if (actual != 5_000_000_016L)
                throw new InvalidOperationException($"Expected 5000000016, got {actual}");
            return actual.ToString();
        });

        await Run(ValidationArea.HostInterop, "logical AND short-circuits host call", async () =>
        {
            int calls = 0;
            await using var shortRuntime = await new TypeSharpRuntimeBuilder()
                .RegisterHostFunction("probe", "sideEffectTrue", _ =>
                {
                    Interlocked.Increment(ref calls);
                    return TsValue.FromBool(true);
                })
                .RegisterHostFunction("probe", "sideEffectFalse", _ =>
                {
                    Interlocked.Increment(ref calls);
                    return TsValue.FromBool(false);
                })
                .AddSourceFile(Script("host", "shortcircuit.ts"))
                .BuildAsync();

            bool result = await shortRuntime.InvokeAsync<bool>("shortcircuit", "andShortCircuit");
            if (result)
                throw new InvalidOperationException("Expected false");
            if (calls != 0)
                throw new InvalidOperationException($"Right operand executed {calls} time(s)");
            return "right operand not evaluated";
        });

        await Run(ValidationArea.HostInterop, "logical OR short-circuits host call", async () =>
        {
            int calls = 0;
            await using var shortRuntime = await new TypeSharpRuntimeBuilder()
                .RegisterHostFunction("probe", "sideEffectTrue", _ =>
                {
                    Interlocked.Increment(ref calls);
                    return TsValue.FromBool(true);
                })
                .RegisterHostFunction("probe", "sideEffectFalse", _ =>
                {
                    Interlocked.Increment(ref calls);
                    return TsValue.FromBool(false);
                })
                .AddSourceFile(Script("host", "shortcircuit.ts"))
                .BuildAsync();

            bool result = await shortRuntime.InvokeAsync<bool>("shortcircuit", "orShortCircuit");
            if (!result)
                throw new InvalidOperationException("Expected true");
            if (calls != 0)
                throw new InvalidOperationException($"Right operand executed {calls} time(s)");
            return "right operand not evaluated";
        });

        await Run(ValidationArea.HostInterop, "unannotated host method is unavailable", async () =>
        {
            try
            {
                await using var hiddenRuntime = await new TypeSharpRuntimeBuilder()
                    .AddHostService("typed", new TypedHostService())
                    .AddSourceFile(Script("host", "hidden.ts"))
                    .BuildAsync();
                throw new InvalidOperationException("Hidden host method was exposed");
            }
            catch (Exception ex) when (!ex.Message.Contains("was exposed", StringComparison.Ordinal))
            {
                return $"rejected with {ex.GetType().Name}";
            }
        });

        await Run(ValidationArea.HostInterop, "ambiguous unqualified host name is rejected", async () =>
        {
            try
            {
                await using var ambiguousRuntime = await new TypeSharpRuntimeBuilder()
                    .RegisterHostFunction("left", "sameName", args => args[0])
                    .RegisterHostFunction("right", "sameName", args => args[0])
                    .AddSourceFile(Script("host", "ambiguous.ts"))
                    .BuildAsync();
                throw new InvalidOperationException("Ambiguous host call compiled");
            }
            catch (Exception ex) when (!ex.Message.Contains("compiled", StringComparison.Ordinal))
            {
                return $"rejected with {ex.GetType().Name}";
            }
        });
    }

    private static async Task RunBytecodeTests()
    {
        await Run(ValidationArea.Bytecode, "compiled bytecode verifies", async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(Script("numeric", "main.ts"));
            var module = runtime.ActiveGeneration!.Modules["numeric"].Bytecode;
            BytecodeVerifier.Verify(module);
            return $"{module.Functions.Length} functions";
        });

        await Run(ValidationArea.Bytecode, "serialize/deserialize round-trip executes", async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(Script("stable", "main.ts"));
            var module = runtime.ActiveGeneration!.Modules["stable"].Bytecode;

            using var stream = new MemoryStream();
            BytecodeSerializer.Serialize(stream, module);
            stream.Position = 0;
            var restored = BytecodeSerializer.Deserialize(stream);

            var interpreter = new Interpreter();
            var result = interpreter.Execute(restored, "forSum", new[] { TsValue.FromInt32(100) });
            int actual = ((TsInt32Value)result!).Value;
            if (actual != 5_050)
                throw new InvalidOperationException($"Expected 5050, got {actual}");
            return $"{stream.Length} bytes";
        });

        await Run(ValidationArea.Bytecode, "decimal constants survive serialization", async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(Script("numeric", "main.ts"));
            var module = runtime.ActiveGeneration!.Modules["numeric"].Bytecode;

            using var stream = new MemoryStream();
            BytecodeSerializer.Serialize(stream, module);
            stream.Position = 0;
            var restored = BytecodeSerializer.Deserialize(stream);

            var interpreter = new Interpreter();
            var result = interpreter.Execute(restored, "decimalPipeline",
                new[] { TsValue.FromDecimal(12.5m), TsValue.FromDecimal(0.25m), TsValue.FromDecimal(3m) });
            decimal actual = ((TsDecimalValue)result!).Value;
            decimal expected = (12.5m + 0.25m) * 3m - 12.5m / 3m;
            if (actual != expected)
                throw new InvalidOperationException($"Expected {expected}, got {actual}");
            return actual.ToString();
        });

        await ExpectBytecodeRejected("unknown opcode", new BytecodeModule("invalid",
            new[] { NewFunction("main", new byte[] { 0xFF }) }));

        await ExpectBytecodeRejected("truncated operand", new BytecodeModule("invalid",
            new[] { NewFunction("main", new byte[] { Opcodes.LoadConstI32, 1, 2 }) }));

        await ExpectBytecodeRejected("invalid local index", new BytecodeModule("invalid",
            new[] { NewFunction("main", Concat(
                new byte[] { Opcodes.LoadLocal }, BitConverter.GetBytes(0), new byte[] { Opcodes.Return })) }));

        await ExpectBytecodeRejected("branch into operand", new BytecodeModule("invalid",
            new[] { NewFunction("main", Concat(
                new byte[] { Opcodes.Branch }, BitConverter.GetBytes(2), new byte[] { Opcodes.Return })) }));

        await Run(ValidationArea.Bytecode, "duplicate function name is rejected", async () =>
        {
            try
            {
                BytecodeVerifier.Verify(new BytecodeModule("duplicate",
                    new[] { ReturnI32("same", 1), ReturnI32("same", 2) }));
                throw new InvalidOperationException("Duplicate functions passed verification");
            }
            catch (BytecodeVerificationException ex)
            {
                return SingleLine(ex.Message);
            }
        });

        await Run(ValidationArea.Bytecode, "invalid serialized magic is rejected", async () =>
        {
            using var stream = new MemoryStream(new byte[32]);
            try
            {
                _ = BytecodeSerializer.Deserialize(stream);
                throw new InvalidOperationException("Invalid magic was accepted");
            }
            catch (BytecodeVerificationException ex)
            {
                return SingleLine(ex.Message);
            }
        });
    }

    private static async Task RunHotReloadTests()
    {
        await Run(ValidationArea.HotReload, "valid direct reload swaps generation", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }");

            int before = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            await fixture.WriteAsync("export function version(): int32 { return 2; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.FilePath);
            int after = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");

            if (!changed || before != 1 || after != 2)
                throw new InvalidOperationException($"changed={changed}, before={before}, after={after}");
            return $"{before} -> {after}";
        });

        await Run(ValidationArea.HotReload, "unchanged source does not create generation", async () =>
        {
            const string source = "export function version(): int32 { return 1; }";
            await using var fixture = await HotReloadFixture.CreateAsync(source);
            int generation = fixture.Runtime.ActiveGeneration!.Id;
            bool changed = await fixture.Runtime.ReloadAsync(fixture.FilePath);
            if (changed)
                throw new InvalidOperationException("Reload reported a change");
            if (fixture.Runtime.ActiveGeneration!.Id != generation)
                throw new InvalidOperationException("Generation changed");
            return $"generation {generation}";
        });

        await Run(ValidationArea.HotReload, "syntax failure preserves active generation", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }");
            int generation = fixture.Runtime.ActiveGeneration!.Id;
            await fixture.WriteAsync("export function version(: int32 { return 2; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.FilePath);
            int value = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            if (changed || value != 1 || fixture.Runtime.ActiveGeneration!.Id != generation)
                throw new InvalidOperationException($"changed={changed}, value={value}");
            return "old generation preserved";
        });

        await Run(ValidationArea.HotReload, "type failure preserves active generation", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }");
            await fixture.WriteAsync("export function version(): int32 { return \"bad\"; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.FilePath);
            int value = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            if (changed || value != 1)
                throw new InvalidOperationException($"changed={changed}, value={value}");
            return "old generation preserved";
        });

        await Run(ValidationArea.HotReload, "valid retry succeeds after failed reload", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }");
            await fixture.WriteAsync("export function version(): int32 { return \"bad\"; }");
            if (await fixture.Runtime.ReloadAsync(fixture.FilePath))
                throw new InvalidOperationException("Invalid candidate activated");

            await fixture.WriteAsync("export function version(): int32 { return 3; }");
            if (!await fixture.Runtime.ReloadAsync(fixture.FilePath))
                throw new InvalidOperationException("Valid retry was ignored");

            int value = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            if (value != 3)
                throw new InvalidOperationException($"Expected 3, got {value}");
            return "retry activated";
        });

        await Run(ValidationArea.HotReload, "startup test rejects candidate returning false", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function test(): bool { return true; } export function version(): int32 { return 1; }");

            await fixture.WriteAsync(
                "export function test(): bool { return false; } export function version(): int32 { return 2; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.FilePath);
            int value = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            if (changed || value != 1)
                throw new InvalidOperationException($"changed={changed}, value={value}");
            return "candidate rejected";
        });

        await Run(ValidationArea.HotReload, "custom canary rejects candidate", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }",
                builder => builder.ConfigureHotReload(options => options.Canary = _ => false));

            await fixture.WriteAsync("export function version(): int32 { return 2; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.FilePath);
            int value = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            if (changed || value != 1)
                throw new InvalidOperationException($"changed={changed}, value={value}");
            return "candidate rejected";
        });

        await Run(ValidationArea.HotReload, "rollback restores previous generation", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }");
            await fixture.WriteAsync("export function version(): int32 { return 2; }");
            if (!await fixture.Runtime.ReloadAsync(fixture.FilePath))
                throw new InvalidOperationException("Reload failed");

            if (!await fixture.Runtime.RollbackAsync())
                throw new InvalidOperationException("Rollback returned false");

            int value = await fixture.Runtime.InvokeAsync<int>(fixture.ModuleName, "version");
            if (value != 1)
                throw new InvalidOperationException($"Expected 1, got {value}");
            return "2 -> 1";
        });

        await Run(ValidationArea.HotReload, "retired generation retention is bounded", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 0; }",
                builder => builder.ConfigureHotReload(options => options.RetainedGenerations = 2));

            for (int i = 1; i <= 6; i++)
            {
                await fixture.WriteAsync($"export function version(): int32 {{ return {i}; }}");
                if (!await fixture.Runtime.ReloadAsync(fixture.FilePath))
                    throw new InvalidOperationException($"Reload {i} failed");
            }

            if (fixture.Runtime.RetiredGenerations.Count > 2)
                throw new InvalidOperationException(
                    $"Retained {fixture.Runtime.RetiredGenerations.Count} generations");
            return $"{fixture.Runtime.RetiredGenerations.Count} retired";
        });

        await Run(ValidationArea.HotReload, "generation lease protects in-flight version", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }",
                builder => builder.ConfigureHotReload(options => options.RetainedGenerations = 1));

            var lease = fixture.Runtime.AcquireGeneration()
                ?? throw new InvalidOperationException("No active generation");
            int oldId = lease.Generation.Id;

            await fixture.WriteAsync("export function version(): int32 { return 2; }");
            if (!await fixture.Runtime.ReloadAsync(fixture.FilePath))
                throw new InvalidOperationException("Reload failed");

            var retired = fixture.Runtime.RetiredGenerations.SingleOrDefault(gen => gen.Id == oldId)
                ?? throw new InvalidOperationException("Old generation was not retained");
            if (retired.ActiveExecutions != 1)
                throw new InvalidOperationException($"Expected one lease, got {retired.ActiveExecutions}");

            lease.Dispose();
            if (retired.ActiveExecutions != 0)
                throw new InvalidOperationException("Lease count did not return to zero");
            return $"generation {oldId} protected";
        });

        await Run(ValidationArea.HotReload, "reload event fires once", async () =>
        {
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }");
            int count = 0;
            fixture.Runtime.ModuleReloaded += (_, _) => Interlocked.Increment(ref count);

            await fixture.WriteAsync("export function version(): int32 { return 2; }");
            if (!await fixture.Runtime.ReloadAsync(fixture.FilePath))
                throw new InvalidOperationException("Reload failed");
            if (count != 1)
                throw new InvalidOperationException($"Expected one event, got {count}");
            return "one event";
        });

        await Run(ValidationArea.HotReload, "dependency reload updates linked caller", async () =>
        {
            await using var fixture = await MultiModuleReloadFixture.CreateAsync();
            int before = await fixture.Runtime.InvokeAsync<int>("app", "currentApp");
            await File.WriteAllTextAsync(fixture.LibraryFile,
                "export function current(): int32 { return 2; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.LibraryFile);
            int after = await fixture.Runtime.InvokeAsync<int>("app", "currentApp");
            if (!changed || before != 1 || after != 2)
                throw new InvalidOperationException($"changed={changed}, before={before}, after={after}");
            return $"{before} -> {after}";
        });

        await Run(ValidationArea.HotReload, "reloading imported entry module keeps imports", async () =>
        {
            await using var fixture = await MultiModuleReloadFixture.CreateAsync();
            await File.WriteAllTextAsync(fixture.AppFile,
                "import { current } from \"../lib/value\"; " +
                "export function currentApp(): int32 { return current() + 10; }");
            bool changed = await fixture.Runtime.ReloadAsync(fixture.AppFile);
            int value = await fixture.Runtime.InvokeAsync<int>("app", "currentApp");
            if (!changed || value != 11)
                throw new InvalidOperationException($"changed={changed}, value={value}");
            return "import survived reload";
        });

        await Run(ValidationArea.HotReload, "reloading host-dependent module keeps host symbols", async () =>
        {
            string directory = NewTempDirectory("typesharp-host-reload");
            string file = Path.Combine(directory, "main.ts");
            await File.WriteAllTextAsync(file,
                "export function value(input: int32): int32 { return hostAdd(input, 1); }");
            try
            {
                await using var runtime = await new TypeSharpRuntimeBuilder()
                    .AddHostService("typed", new TypedHostService())
                    .AddSourceFile(file)
                    .EnableHotReload()
                    .BuildAsync();

                string module = Path.GetFileName(directory);
                await File.WriteAllTextAsync(file,
                    "export function value(input: int32): int32 { return hostAdd(input, 2); }");
                bool changed = await runtime.ReloadAsync(file);
                int value = await runtime.InvokeAsync<int>(module, "value", 10);
                if (!changed || value != 12)
                    throw new InvalidOperationException($"changed={changed}, value={value}");
                return "host symbols survived reload";
            }
            finally
            {
                DeleteDirectory(directory);
            }
        });

        await Run(ValidationArea.HotReload, "generation migrator runs exactly once", async () =>
        {
            var migrator = new CountingMigrator();
            await using var fixture = await HotReloadFixture.CreateAsync(
                "export function version(): int32 { return 1; }",
                builder => builder.ConfigureHotReload(options => options.Migrators.Add(migrator)));

            await fixture.WriteAsync("export function version(): int32 { return 2; }");
            if (!await fixture.Runtime.ReloadAsync(fixture.FilePath))
                throw new InvalidOperationException("Reload failed");
            if (migrator.CanMigrateCalls != 1 || migrator.MigrateCalls != 1)
                throw new InvalidOperationException(
                    $"CanMigrate={migrator.CanMigrateCalls}, Migrate={migrator.MigrateCalls}");
            return "one migration";
        });
    }

    private static async Task RunIsolationAndLimitTests()
    {
        await Run(ValidationArea.Isolation, "5,000 sequential calls do not leak VM state", async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(Script("stable", "main.ts"));
            for (int i = 0; i < 5_000; i++)
            {
                int value = await runtime.InvokeAsync<int>("stable", "forwardReference", i);
                if (value != i * 5 + 2)
                    throw new InvalidOperationException($"Iteration {i}: got {value}");
            }
            return "5,000 calls";
        });

        await Run(ValidationArea.Isolation, "256 parallel calls remain isolated", async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(Script("stable", "main.ts"));
            var failures = new ConcurrentQueue<string>();

            Task[] tasks = Enumerable.Range(0, 256).Select(async i =>
            {
                try
                {
                    int actual = await runtime.InvokeAsync<int>("stable", "branchMerge", i % 10);
                    int expected = (i % 10) > 5 ? 34 : 14;
                    if (actual != expected)
                        failures.Enqueue($"{i}: expected {expected}, got {actual}");
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{i}: {ex.GetType().Name}: {ex.Message}");
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            if (!failures.IsEmpty)
                throw new InvalidOperationException(string.Join(" | ", failures.Take(10)));
            return "256 calls";
        });

        await Run(ValidationArea.Isolation, "independent runtimes do not share functions", async () =>
        {
            string leftDir = NewTempDirectory("typesharp-left");
            string rightDir = NewTempDirectory("typesharp-right");
            string leftFile = Path.Combine(leftDir, "main.ts");
            string rightFile = Path.Combine(rightDir, "main.ts");
            await File.WriteAllTextAsync(leftFile, "export function value(): int32 { return 11; }");
            await File.WriteAllTextAsync(rightFile, "export function value(): int32 { return 29; }");
            try
            {
                await using var left = await new TypeSharpRuntimeBuilder().AddSourceFile(leftFile).BuildAsync();
                await using var right = await new TypeSharpRuntimeBuilder().AddSourceFile(rightFile).BuildAsync();
                int leftValue = await left.InvokeAsync<int>(Path.GetFileName(leftDir), "value");
                int rightValue = await right.InvokeAsync<int>(Path.GetFileName(rightDir), "value");
                if (leftValue != 11 || rightValue != 29)
                    throw new InvalidOperationException($"left={leftValue}, right={rightValue}");
                return "11 / 29";
            }
            finally
            {
                DeleteDirectory(leftDir);
                DeleteDirectory(rightDir);
            }
        });

        await Run(ValidationArea.Limits, "recursion limit failure does not poison next call", async () =>
        {
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(Script("limits", "recursion.ts"))
                .ConfigureLimits(options =>
                {
                    options.MaximumRecursionDepth = 32;
                    options.MaximumInstructions = 1_000_000;
                    options.ExecutionTimeout = TimeSpan.FromSeconds(2);
                })
                .BuildAsync();

            try
            {
                _ = await runtime.InvokeAsync<int>("recursion", "recurse", 0);
                throw new InvalidOperationException("Recursive call completed");
            }
            catch (Exception ex) when (!ex.Message.Contains("completed", StringComparison.Ordinal))
            {
                int safe = await runtime.InvokeAsync<int>("recursion", "safe");
                if (safe != 42)
                    throw new InvalidOperationException($"Safe call returned {safe}");
                return $"{ex.GetType().Name}; safe=42";
            }
        });

        await Run(ValidationArea.Limits, "infinite loop is interrupted", async () =>
        {
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(Script("limits", "infinite.ts"))
                .ConfigureLimits(options =>
                {
                    options.MaximumInstructions = 20_000;
                    options.ExecutionTimeout = TimeSpan.FromMilliseconds(500);
                })
                .BuildAsync();

            try
            {
                _ = await runtime.InvokeAsync<int>("infinite", "spin");
                throw new InvalidOperationException("Infinite loop completed");
            }
            catch (Exception ex) when (!ex.Message.Contains("completed", StringComparison.Ordinal))
            {
                return $"{ex.GetType().Name}: {SingleLine(ex.Message)}";
            }
        });

        await Run(ValidationArea.Limits, "logical memory budget resets between executions", async () =>
        {
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(Script("stable", "main.ts"))
                .ConfigureLimits(options =>
                {
                    options.MaximumMemoryBytes = 1_024;
                    options.MaximumInstructions = 1_000_000;
                    options.ExecutionTimeout = TimeSpan.FromSeconds(2);
                })
                .BuildAsync();

            for (int i = 0; i < 100; i++)
            {
                int actual = await runtime.InvokeAsync<int>("stable", "objectPerCall", i);
                if (actual != i * 2 + 1)
                    throw new InvalidOperationException($"Iteration {i}: got {actual}");
            }
            return "100 isolated heaps";
        });
    }

    private static async Task<TypeSharpRuntime> BuildSingleFileRuntime(string file)
    {
        return await new TypeSharpRuntimeBuilder()
            .AddSourceFile(file)
            .ConfigureLimits(options =>
            {
                options.ExecutionTimeout = TimeSpan.FromSeconds(5);
                options.MaximumInstructions = 30_000_000;
                options.MaximumMemoryBytes = 128 * 1024 * 1024;
                options.MaximumRecursionDepth = 1_024;
            })
            .BuildAsync();
    }

    private static async Task RunDirectoryProbe<T>(
        ValidationArea area,
        string name,
        string directory,
        string module,
        string function,
        T expected,
        params object[] args)
    {
        await Run(area, name, async () =>
        {
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(directory)
                .BuildAsync();
            T actual = await runtime.InvokeAsync<T>(module, function, args);
            AssertEqual(expected, actual);
            return Format(actual);
        });
    }

    private static async Task RunScriptProbe<T>(
        ValidationArea area,
        string name,
        string file,
        string module,
        string function,
        T expected,
        params object[] args)
    {
        await Run(area, name, async () =>
        {
            await using var runtime = await BuildSingleFileRuntime(file);
            T actual = await runtime.InvokeAsync<T>(module, function, args);
            AssertEqual(expected, actual);
            return Format(actual);
        });
    }

    private static async Task Expect<T>(
        TypeSharpRuntime runtime,
        ValidationArea area,
        string name,
        string module,
        string function,
        T expected,
        params object[] args)
    {
        await Run(area, name, async () =>
        {
            T actual = await runtime.InvokeAsync<T>(module, function, args);
            AssertEqual(expected, actual);
            return Format(actual);
        });
    }

    private static async Task ExpectFloat(
        TypeSharpRuntime runtime,
        ValidationArea area,
        string name,
        string module,
        string function,
        float expected,
        params object[] args)
    {
        await Run(area, name, async () =>
        {
            float actual = await runtime.InvokeAsync<float>(module, function, args);
            if (MathF.Abs(actual - expected) > 0.00001f)
                throw new InvalidOperationException($"Expected {expected:R}, got {actual:R}");
            return actual.ToString("R");
        });
    }

    private static async Task ExpectCompileRejected(string name, string file)
    {
        await Run(ValidationArea.TypeSafety, name, async () =>
        {
            try
            {
                await using var runtime = await BuildSingleFileRuntime(file);
                throw new InvalidOperationException("Invalid source compiled successfully");
            }
            catch (Exception ex) when (!ex.Message.Contains("compiled successfully", StringComparison.Ordinal))
            {
                return $"rejected with {ex.GetType().Name}: {SingleLine(ex.Message)}";
            }
        });
    }

    private static async Task ExpectDirectoryCompileRejected(string name, string directory)
    {
        await Run(ValidationArea.Modules, name, async () =>
        {
            try
            {
                await using var runtime = await new TypeSharpRuntimeBuilder()
                    .AddSourceDirectory(directory)
                    .BuildAsync();
                throw new InvalidOperationException("Invalid module graph compiled successfully");
            }
            catch (Exception ex) when (!ex.Message.Contains("compiled successfully", StringComparison.Ordinal))
            {
                return $"rejected with {ex.GetType().Name}: {SingleLine(ex.Message)}";
            }
        });
    }

    private static async Task ExpectBytecodeRejected(string name, BytecodeModule module)
    {
        await Run(ValidationArea.Bytecode, name, async () =>
        {
            try
            {
                BytecodeVerifier.Verify(module);
                throw new InvalidOperationException("Invalid bytecode passed verification");
            }
            catch (BytecodeVerificationException ex)
            {
                return SingleLine(ex.Message);
            }
        });
    }

    private static async Task Run(ValidationArea area, string name, Func<Task<string>> body)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            string details = await body();
            TimeSpan elapsed = DateTime.UtcNow - started;
            Results.Add(new ValidationResult(area, name, true, details, elapsed));
            Console.WriteLine($"[PASS] {area,-12} {name} ({elapsed.TotalMilliseconds:N0} ms)");
        }
        catch (Exception ex)
        {
            TimeSpan elapsed = DateTime.UtcNow - started;
            string details = $"{ex.GetType().Name}: {SingleLine(ex.Message)}";
            Results.Add(new ValidationResult(area, name, false, details, elapsed));
            Console.WriteLine($"[FAIL] {area,-12} {name} ({elapsed.TotalMilliseconds:N0} ms)");
            Console.WriteLine($"       {details}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private static string Format<T>(T value) => value?.ToString() ?? "null";

    private static string ResolveScriptsRoot()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "scripts");
        if (Directory.Exists(output))
            return output;

        string local = Path.Combine(Directory.GetCurrentDirectory(), "scripts");
        if (Directory.Exists(local))
            return local;

        throw new DirectoryNotFoundException("Could not locate scripts directory");
    }

    private static string Script(params string[] parts) =>
        parts.Aggregate(ScriptsRoot, Path.Combine);

    private static string GetOnlyModuleName(TypeSharpRuntime runtime)
    {
        return runtime.ActiveGeneration!.Modules.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .First();
    }

    private static string CreateTemporaryScript(string source)
    {
        string directory = NewTempDirectory("typesharp-script");
        string file = Path.Combine(directory, "main.ts");
        File.WriteAllText(file, source);
        TemporaryPaths.Add(directory);
        return file;
    }

    private static readonly ConcurrentBag<string> TemporaryPaths = new();

    private static string NewTempDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static string SingleLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static BytecodeFunction NewFunction(string name, byte[] instructions, int parameters = 0, int locals = 0) =>
        new(name, instructions, parameters, locals, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>(), Array.Empty<decimal>());

    private static BytecodeFunction ReturnI32(string name, int value) =>
        NewFunction(name, Concat(new byte[] { Opcodes.LoadConstI32 }, BitConverter.GetBytes(value),
            new byte[] { Opcodes.Return }));

    private static byte[] Concat(params byte[][] arrays)
    {
        int length = arrays.Sum(array => array.Length);
        var result = new byte[length];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }
        return result;
    }

    private static void PrintSummary()
    {
        foreach (string path in TemporaryPaths)
            DeleteDirectory(path);

        Console.WriteLine();
        Console.WriteLine(new string('=', 84));
        Console.WriteLine("SUMMARY");
        Console.WriteLine(new string('-', 84));

        foreach (IGrouping<ValidationArea, ValidationResult> group in Results.GroupBy(result => result.Area))
        {
            int passed = group.Count(result => result.Passed);
            Console.WriteLine($"{group.Key,-12}: {passed}/{group.Count()} passed");
        }

        int totalPassed = Results.Count(result => result.Passed);
        Console.WriteLine(new string('-', 84));
        Console.WriteLine($"TOTAL       : {totalPassed}/{Results.Count} passed");

        ValidationResult[] failures = Results.Where(result => !result.Passed).ToArray();
        if (failures.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("FAILURES TO RETURN:");
            foreach (ValidationResult failure in failures)
                Console.WriteLine($"- [{failure.Area}] {failure.Name}: {failure.Details}");
        }
    }

    private sealed class TypedHostService
    {
        [TsExport("hostAdd")]
        public int Add(int left, int right) => left + right;

        [TsExport("hostMix64")]
        public long Mix64(long left, long right) => left + right;

        [TsExport("hostEchoUInt64")]
        public ulong EchoUInt64(ulong value) => value;

        [TsExport("hostDecimalAdd")]
        public decimal DecimalAdd(decimal left, decimal right) => left + right;

        [TsExport("hostFloatScale")]
        public float FloatScale(float value) => value * 2.5f;

        [TsExport("hostNegate")]
        public bool Negate(bool value) => !value;

        [TsExport("hostDecorate")]
        public string Decorate(string value) => $"[{value}]";

        [TsExport("hostAsyncDouble")]
        public Task<int> AsyncDouble(int value) => Task.FromResult(value * 2);

        [TsExport("hostAsyncIncrement")]
        public ValueTask<long> AsyncIncrement(long value) => ValueTask.FromResult(value + 1);

        [TsExport("hostThrow")]
        public int Throw(int value) => throw new InvalidOperationException($"host failure {value}");

        public int HiddenMethod(int value) => value + 1000;
    }

    private sealed class CountingMigrator : IGenerationMigrator
    {
        public int CanMigrateCalls;
        public int MigrateCalls;

        public bool CanMigrate(RuntimeGeneration previous, RuntimeGeneration candidate)
        {
            Interlocked.Increment(ref CanMigrateCalls);
            return true;
        }

        public void Migrate(RuntimeGeneration previous, RuntimeGeneration candidate)
        {
            Interlocked.Increment(ref MigrateCalls);
        }
    }

    private sealed class HotReloadFixture : IAsyncDisposable
    {
        public string DirectoryPath { get; }
        public string FilePath { get; }
        public string ModuleName { get; }
        public TypeSharpRuntime Runtime { get; }

        private HotReloadFixture(string directoryPath, string filePath, TypeSharpRuntime runtime)
        {
            DirectoryPath = directoryPath;
            FilePath = filePath;
            ModuleName = Path.GetFileName(directoryPath);
            Runtime = runtime;
        }

        public static async Task<HotReloadFixture> CreateAsync(
            string source,
            Action<TypeSharpRuntimeBuilder>? configure = null)
        {
            string directory = NewTempDirectory("typesharp-hot");
            string file = Path.Combine(directory, "main.ts");
            await File.WriteAllTextAsync(file, source);

            var builder = new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload();
            configure?.Invoke(builder);
            TypeSharpRuntime runtime = await builder.BuildAsync();
            return new HotReloadFixture(directory, file, runtime);
        }

        public Task WriteAsync(string source) => File.WriteAllTextAsync(FilePath, source);

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            DeleteDirectory(DirectoryPath);
        }
    }

    private sealed class MultiModuleReloadFixture : IAsyncDisposable
    {
        public string DirectoryPath { get; }
        public string LibraryFile { get; }
        public string AppFile { get; }
        public TypeSharpRuntime Runtime { get; }

        private MultiModuleReloadFixture(
            string directoryPath,
            string libraryFile,
            string appFile,
            TypeSharpRuntime runtime)
        {
            DirectoryPath = directoryPath;
            LibraryFile = libraryFile;
            AppFile = appFile;
            Runtime = runtime;
        }

        public static async Task<MultiModuleReloadFixture> CreateAsync()
        {
            string directory = NewTempDirectory("typesharp-multi");
            string libDirectory = Path.Combine(directory, "lib");
            string appDirectory = Path.Combine(directory, "app");
            Directory.CreateDirectory(libDirectory);
            Directory.CreateDirectory(appDirectory);

            string library = Path.Combine(libDirectory, "value.ts");
            string app = Path.Combine(appDirectory, "main.ts");
            await File.WriteAllTextAsync(library,
                "export function current(): int32 { return 1; }");
            await File.WriteAllTextAsync(app,
                "import { current } from \"../lib/value\"; " +
                "export function currentApp(): int32 { return current(); }");

            TypeSharpRuntime runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(directory)
                .EnableHotReload()
                .BuildAsync();

            return new MultiModuleReloadFixture(directory, library, app, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            DeleteDirectory(DirectoryPath);
        }
    }
}
