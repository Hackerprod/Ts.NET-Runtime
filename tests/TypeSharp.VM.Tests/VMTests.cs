using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using TypeSharp.IR;
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
        var initial = heap.BytesAllocated;
        heap.AllocateObject("Test");
        heap.AllocateArray();
        Assert.True(heap.BytesAllocated > initial);
    }
}
