using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using TypeSharp.IR;
using TypeSharp.Interop.HostExports;
using TypeSharp.Interop.Marshalling;
using TypeSharp.Interop.Proxies;
using Xunit;

namespace TypeSharp.VM.Tests;

public class InterpreterTests
{
    private static TsValue? Run(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);

        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);

        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }

    [Fact]
    public void ExecuteAddFunction()
    {
        var code = "function add(a: int32, b: int32): int32 { return a + b; }";
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IR.IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = Bytecode.BytecodeCompiler.Compile(moduleIR);

        foreach (var f in bytecodeModule.Functions)
        {
            Console.Error.WriteLine($"  {f.Name}: {f.Instructions.Length} bytes, Params={f.ParameterCount}, Locals={f.LocalCount}");
            Console.Error.WriteLine($"    Bytecode: {BitConverter.ToString(f.Instructions)}");
        }
        foreach (var f in moduleIR.Functions)
        {
            foreach (var b in f.Blocks)
            {
                Console.Error.WriteLine($"    Block {b.Id}:");
                foreach (var instr in b.Instructions)
                    Console.Error.WriteLine($"      {instr.Opcode} op0={instr.Operand0} opObj={instr.OperandObject}");
            }
        }

        var result = Run(code, "add", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteReturnConstant()
    {
        var result = Run(@"
            function main(): int32 {
                return 42;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(42, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteVariableAssignment()
    {
        var result = Run(@"
            function main(): int32 {
                let x: int32 = 10;
                let y: int32 = 20;
                return x + y;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(30, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteStringConcat()
    {
        var result = Run(@"
            function main(): string {
                return ""Hello "" + ""World"";
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("Hello World", ((TsStringValue)result).Value);
    }

    [Fact]
    public void ExecuteIfElse()
    {
        var result = Run(@"
            function main(): int32 {
                let x: int32 = 5;
                if (x > 3) {
                    return 1;
                } else {
                    return 0;
                }
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteWhileLoop()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 1;
                while (i <= 10) {
                    sum = sum + i;
                    i = i + 1;
                }
                return sum;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(55, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteBooleanOperations()
    {
        var result = Run(@"
            function main(): int32 {
                let a: bool = true;
                let b: bool = false;
                if (a && !b) {
                    return 1;
                }
                return 0;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteNestedCalls()
    {
        var code = @"
            function double_it(x: int32): int32 {
                return x + x;
            }
            function main(): int32 {
                return double_it(double_it(5));
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(20, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassConstructor()
    {
        var code = @"
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
                const c = new Counter(5);
                return c.getCount();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(5, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassMethodMutatingFields()
    {
        var code = @"
            class Counter {
                count: int32;
                constructor(initial: int32) {
                    this.count = initial;
                }
                increment(): void {
                    this.count = this.count + 1;
                }
                getCount(): int32 {
                    return this.count;
                }
            }
            function main(): int32 {
                const c = new Counter(5);
                c.increment();
                c.increment();
                c.increment();
                return c.getCount();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(8, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassMultipleFields()
    {
        var code = @"
            class Point {
                x: int32;
                y: int32;
                constructor(x: int32, y: int32) {
                    this.x = x;
                    this.y = y;
                }
                sum(): int32 {
                    return this.x + this.y;
                }
            }
            function main(): int32 {
                const p = new Point(3, 4);
                return p.sum();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassStringField()
    {
        var code = @"
            class Greeter {
                greeting: string;
                constructor(name: string) {
                    this.greeting = ""Hello "" + name;
                }
                getGreeting(): string {
                    return this.greeting;
                }
            }
            function main(): string {
                const g = new Greeter(""World"");
                return g.getGreeting();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("Hello World", ((TsStringValue)result).Value);
    }

    [Fact]
    public void ExecuteClassMultipleInstances()
    {
        var code = @"
            class Counter {
                count: int32;
                constructor(initial: int32) {
                    this.count = initial;
                }
                increment(): void {
                    this.count = this.count + 1;
                }
                getCount(): int32 {
                    return this.count;
                }
            }
            function main(): int32 {
                const a = new Counter(0);
                const b = new Counter(10);
                a.increment();
                a.increment();
                b.increment();
                return a.getCount() + b.getCount();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(13, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteSubtractFunction()
    {
        var code = @"
            function subtract(a: int32, b: int32): int32 {
                return a - b;
            }
            function main(): int32 {
                return subtract(10, 3);
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteUInt64Arithmetic()
    {
        var code = @"
            function main(): uint64 {
                const a: uint64 = 100u64;
                const b: uint64 = 200u64;
                return a + b;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(300UL, ((TsUInt64Value)result).Value);
    }

    [Fact]
    public void ExecuteDecimalArithmetic()
    {
        var code = @"
            function main(): decimal {
                const a: decimal = 1.5m;
                const b: decimal = 2.5m;
                return a + b;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsDecimalValue>(result);
        Assert.Equal(4.0m, ((TsDecimalValue)result).Value);
    }

    [Fact]
    public void ExecuteDecimalMultiply()
    {
        var code = @"
            function main(): decimal {
                const a: decimal = 3m;
                const b: decimal = 7m;
                return a * b;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsDecimalValue>(result);
        Assert.Equal(21.0m, ((TsDecimalValue)result).Value);
    }

    [Fact]
    public void ExecuteThrowString()
    {
        var code = @"
            function main(): string {
                throw ""something broke"";
            }
        ";

        var ex = Record.Exception(() => Run(code, "main"));
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("something broke", ex.Message);
    }

    [Fact]
    public void ExecuteInt64WithBranches()
    {
        var code = @"
            function main(): int64 {
                const big: int64 = 9999999999i64;
                if (big > 100i64) {
                    return big;
                }
                return 0i64;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt64Value>(result);
        Assert.Equal(9999999999L, ((TsInt64Value)result).Value);
    }

    [Fact]
    public void ExecuteUint64WithBranches()
    {
        var code = @"
            function main(): uint64 {
                const big: uint64 = 18446744073709551615u64;
                if (big > 100u64) {
                    return big;
                }
                return 0u64;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(18446744073709551615UL, ((TsUInt64Value)result).Value);
    }
}

public class TypeCheckingTests
{
    [Fact]
    public void StringPlusIntProducesVoidType()
    {
        var code = @"
            function main(): string {
                return ""hello"" + 5;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var boundTree = binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("cannot be applied to types"));
    }

    [Fact]
    public void StringPlusStringWorks()
    {
        var result = Run(@"
            function main(): string {
                return ""hello "" + ""world"";
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("hello world", ((TsStringValue)result).Value);
    }

    [Fact]
    public void WrongArgumentCountProducesError()
    {
        var code = @"
            function add(a: int32, b: int32): int32 {
                return a + b;
            }
            function main(): int32 {
                return add(1);
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var boundTree = binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("Expected 2") && d.Message.Contains("but got 1"));
    }

    private static TsValue? Run(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IR.IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = Bytecode.BytecodeCompiler.Compile(moduleIR);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }
}

public class BytecodeTests
{
    [Fact]
    public void CompileAndDecompile()
    {
        var ir = new ModuleIR("test");
        var func = new FunctionIR("add", TypeSharp.Semantics.TypeSystem.TsType.Int32,
            new List<ParameterInfo>
            {
                new("a", TypeSharp.Semantics.TypeSystem.TsType.Int32),
                new("b", TypeSharp.Semantics.TypeSystem.TsType.Int32),
            });

        var block = func.CreateBlock();
        block.Instructions.Add(new Instruction(Opcode.LoadArg, 0));
        block.Instructions.Add(new Instruction(Opcode.LoadArg, 1));
        block.Instructions.Add(new Instruction(Opcode.Add_I32));
        block.Instructions.Add(new Instruction(Opcode.Return));

        ir.AddFunction(func);

        var module = BytecodeCompiler.Compile(ir);
        Assert.Single(module.Functions);
        Assert.Equal("add", module.Functions[0].Name);
        Assert.True(module.Functions[0].Instructions.Length > 0);
    }
}

public class HeapTests
{
    [Fact]
    public void AllocateAndReadObject()
    {
        var heap = new TsHeap();
        var obj = heap.AllocateObject("User");
        obj.SetField("name", TsValue.FromString("Alice"));
        obj.SetField("id", TsValue.FromInt32(1));

        Assert.Equal("User", obj.TypeName);
        Assert.Equal("Alice", ((TsStringValue)obj.GetField("name")).Value);
        Assert.Equal(1, ((TsInt32Value)obj.GetField("id")).Value);
    }

    [Fact]
    public void AllocateArray()
    {
        var heap = new TsHeap();
        var arr = heap.AllocateArray(8);
        arr.Add(TsValue.FromInt32(10));
        arr.Add(TsValue.FromInt32(20));

        Assert.Equal(2, arr.Count);
        Assert.Equal(10, ((TsInt32Value)arr.Get(0)).Value);
        Assert.Equal(20, ((TsInt32Value)arr.Get(1)).Value);
    }

    [Fact]
    public void AllocateMap()
    {
        var heap = new TsHeap();
        var map = heap.AllocateMap();
        map.Set("key1", TsValue.FromString("value1"));
        map.Set("key2", TsValue.FromInt32(42));

        Assert.Equal(2, map.Count);
        Assert.Equal("value1", ((TsStringValue)map.Get("key1")).Value);
        Assert.Equal(42, ((TsInt32Value)map.Get("key2")).Value);
    }

    [Fact]
    public void TrackBytesAllocated()
    {
        var heap = new TsHeap();
        var initial = heap.LogicalBytes;
        heap.AllocateObject("Test");
        Assert.True(heap.LogicalBytes > initial);
    }

    public class SampleService
    {
        [TsExport("add")]
        public int Add(int a, int b) => a + b;

        public int Subtract(int a, int b) => a - b;

        public override string ToString() => "SampleService";
        public override int GetHashCode() => 42;
    }

    [Fact]
    public void ExplicitOnly_OnlyExportsMarkedMethods()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.ExplicitOnly);

        Assert.Single(registry.Functions);
        Assert.True(registry.Functions.ContainsKey("math.add"));
    }

    [Fact]
    public void Public_ExcludesObjectMethods()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.Public);

        Assert.True(registry.Functions.ContainsKey("math.add"));
        Assert.True(registry.Functions.ContainsKey("math.Subtract"));
        Assert.False(registry.Functions.ContainsKey("math.ToString"));
        Assert.False(registry.Functions.ContainsKey("math.GetHashCode"));
        Assert.False(registry.Functions.ContainsKey("math.Equals"));
        Assert.False(registry.Functions.ContainsKey("math.GetType"));
    }

    [Fact]
    public void All_ExportsEverything()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.All);

        Assert.True(registry.Functions.ContainsKey("math.add"));
        Assert.True(registry.Functions.ContainsKey("math.Subtract"));
        Assert.True(registry.Functions.ContainsKey("math.ToString"));
        Assert.True(registry.Functions.ContainsKey("math.GetHashCode"));
        Assert.False(registry.Functions.ContainsKey("math.Equals"));
        Assert.False(registry.Functions.ContainsKey("math.GetType"));
    }

    [Fact]
    public void ExplicitOnly_DoesNotExportUnmarkedPublic()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.ExplicitOnly);

        Assert.False(registry.Functions.ContainsKey("math.Subtract"));
    }
}

public class InteropTests
{
    public class MathService
    {
        [TsExport("add")]
        public int Add(int a, int b) => a + b;

        [TsExport("multiply")]
        public int Multiply(int a, int b) => a * b;

        [TsExport("get_decimal")]
        public decimal GetDecimal() => 123.456m;

        [TsExport("get_ulong")]
        public ulong GetUlong() => ulong.MaxValue;

        [TsExport("echo_string")]
        public string EchoString(string s) => s;

        [TsExport("get_async_value")]
        public Task<int> GetAsyncValue() => Task.FromResult(42);

        [TsExport("get_async_string")]
        public ValueTask<string> GetAsyncString() => new ValueTask<string>("hello");

        [TsExport("get_void_task")]
        public Task DoWorkAsync()
        {
            return Task.CompletedTask;
        }

        [TsExport("is_even")]
        public bool IsEven(int n) => n % 2 == 0;
    }

    public class StringService
    {
        [TsExport("get")]
        public string Get(string key) => $"value:{key}";

        [TsExport("concat")]
        public string Concat(string a, string b) => a + b;
    }

    [Fact]
    public void ModuleIdentity_UsesFullKey()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "add");
        Assert.NotNull(desc);
        Assert.Equal("math", desc.ModuleName);
        Assert.Equal("add", desc.FunctionName);
    }

    [Fact]
    public void NoCollision_DifferentModulesSameMethodName()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);
        registry.RegisterObject("strings", new StringService(), ExportMode.ExplicitOnly);

        var mathGet = registry.GetFunction("math", "add");
        var stringGet = registry.GetFunction("strings", "get");

        Assert.NotNull(mathGet);
        Assert.NotNull(stringGet);
        Assert.NotEqual(mathGet.Implementation, stringGet.Implementation);
    }

    [Fact]
    public void InterpreterRegistration_UsesFullKey()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);
        registry.RegisterObject("strings", new StringService(), ExportMode.ExplicitOnly);

        var proxy = new TypeSharpProxy(registry);

        var mathResult = proxy.InvokeHostFunction("math", "add", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });
        Assert.Equal(7, ((TsInt32Value)mathResult!).Value);

        var stringResult = proxy.InvokeHostFunction("strings", "get", new[] { TsValue.FromString("key1") });
        Assert.Equal("value:key1", ((TsStringValue)stringResult!).Value);

        var crossResult = proxy.InvokeHostFunction("math", "get_decimal", Array.Empty<TsValue>());
        Assert.NotNull(crossResult);
        Assert.Equal(123.456m, ((TsDecimalValue)crossResult!).Value);
    }

    [Fact]
    public void DecimalMarshalling_RoundTrip()
    {
        var input = 123.456m;
        var tsValue = Marshaller.FromManaged(input);
        Assert.IsType<TsDecimalValue>(tsValue);

        var output = Marshaller.ToManaged(tsValue, typeof(decimal));
        Assert.Equal(input, output);
    }

    [Fact]
    public void DecimalMarshalling_PreservesPrecision()
    {
        var input = 123456789.123456789m;
        var tsValue = Marshaller.FromManaged(input);
        var output = Marshaller.ToManaged(tsValue, typeof(decimal));
        Assert.Equal(input, output);
    }

    [Fact]
    public void UInt64Marshalling_RoundTrip()
    {
        var input = ulong.MaxValue;
        var tsValue = Marshaller.FromManaged(input);
        Assert.IsType<TsUInt64Value>(tsValue);

        var output = Marshaller.ToManaged(tsValue, typeof(ulong));
        Assert.Equal(input, output);
    }

    [Fact]
    public void UInt64Marshalling_FromInt32()
    {
        var tsValue = TsValue.FromInt32(42);
        var output = Marshaller.ToManaged(tsValue, typeof(ulong));
        Assert.Equal(42UL, output);
    }

    [Fact]
    public void DTO_WrapsAsObject()
    {
        var dto = new { Name = "test", Value = 42 };
        var tsValue = Marshaller.FromManaged(dto);

        Assert.IsType<TsObjectValue>(tsValue);
        var obj = ((TsObjectValue)tsValue).Value;
        Assert.Equal("test", ((TsStringValue)obj.GetField("Name")).Value);
        Assert.Equal(42, ((TsInt32Value)obj.GetField("Value")).Value);
    }

    [Fact]
    public void AsyncMethod_ReturnsResult()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_async_value");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        Assert.Equal(42, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void AsyncMethod_ValueTask_ReturnsResult()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_async_string");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        Assert.Equal("hello", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void AsyncMethod_VoidTask_DoesNotThrow()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_void_task");
        Assert.NotNull(desc);

        var ex = Record.Exception(() => desc!.Implementation(Array.Empty<TsValue>()));
        Assert.Null(ex);
    }

    [Fact]
    public void Proxy_InvokesCorrectModule()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);
        registry.RegisterObject("strings", new StringService(), ExportMode.ExplicitOnly);

        var proxy = new TypeSharpProxy(registry);

        var mathResult = proxy.InvokeHostFunction("math", "add", new[] { TsValue.FromInt32(10), TsValue.FromInt32(20) });
        Assert.NotNull(mathResult);
        Assert.Equal(30, ((TsInt32Value)mathResult!).Value);

        var stringResult = proxy.InvokeHostFunction("strings", "get", new[] { TsValue.FromString("foo") });
        Assert.NotNull(stringResult);
        Assert.Equal("value:foo", ((TsStringValue)stringResult!).Value);
    }

    [Fact]
    public void Proxy_MissingFunction_ReturnsNull()
    {
        var registry = new HostRegistry();
        var proxy = new TypeSharpProxy(registry);

        var result = proxy.InvokeHostFunction("nonexistent", "func", Array.Empty<TsValue>());
        Assert.Null(result);
    }

    [Fact]
    public void InvokeHostFunction_IsEven_True()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "is_even");
        Assert.NotNull(desc);

        var result = desc!.Implementation(new[] { TsValue.FromInt32(4) });
        Assert.NotNull(result);
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void InvokeHostFunction_IsEven_False()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "is_even");
        Assert.NotNull(desc);

        var result = desc!.Implementation(new[] { TsValue.FromInt32(3) });
        Assert.NotNull(result);
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void DynamicHostProxy_Invokes()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var generator = new HostProxyGenerator(registry);
        var proxy = generator.GenerateInterfaceProxy(typeof(MathService), "math");

        var result = ((DynamicHostProxy)proxy).InvokeMethod("add", TsValue.FromInt32(5), TsValue.FromInt32(7));
        Assert.NotNull(result);
        Assert.Equal(12, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DecimalMarshalling_ViaHostFunction()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_decimal");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        Assert.IsType<TsDecimalValue>(result);
        Assert.Equal(123.456m, ((TsDecimalValue)result!).Value);
    }

    [Fact]
    public void UInt64Marshalling_ViaHostFunction()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_ulong");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(ulong.MaxValue, ((TsUInt64Value)result!).Value);
    }
}
