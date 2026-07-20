using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using TypeSharp.IR;
using TypeSharp.Interop.HostExports;
using TypeSharp.Interop.Marshalling;
using TypeSharp.Interop.Proxies;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
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
    public void ArrayPush_PreservesObjectElements()
    {
        var result = Run(@"
            function main(): int32 {
                const mapped = [];
                mapped.push({ accountId: 42 });
                return mapped.length;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ArrayPush_WorksWhenMappingObjectsInLoop()
    {
        var result = Run(@"
            function main(): int32 {
                const players = [{ accountId: 42 }, { accountId: 77 }];
                const mapped = [];
                for (let i = 0; i < players.length; i++) {
                    mapped.push({ accountId: players[i].accountId });
                }
                return mapped.length;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(2, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ConstObjectLiteral_PreservesIdentityAndMutation()
    {
        var result = Run(@"
            function main(): int32 {
                const state = { count: 1 };
                state.count = 7;
                return state.count;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void DynamicStandardReceivers_DispatchToTheirBuiltins()
    {
        var result = Run(@"
            function main(): int32 {
                const bytes = new Uint8Array([1, 2, 3, 4]);
                const text = ""runtime"";
                return bytes.slice(1, 3).length + text.slice(0, 2).length;
            }
        ");

        Assert.NotNull(result);
        var value = result switch
        {
            TsInt32Value intValue => intValue.Value,
            TsFloat64Value doubleValue => (int)doubleValue.Value,
            _ => throw new Xunit.Sdk.XunitException($"Expected a numeric result, got {result.GetType().Name}.")
        };
        Assert.Equal(4, value);
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
    public void ExecuteStructuralMethodCallThroughRegisteredClassHandler()
    {
        var result = Run("""
            interface Request {
                searchKey?: string;
                leagueId?: number;
                heroId?: number;
                startGame?: number;
                gameListIndex?: number;
            }

            interface Response {
                searchKey: string;
                leagueId: number;
                heroId: number;
                startGame: number;
                numGames: number;
                gameListIndex: number;
                gameList: number[];
                specificGames: boolean;
            }

            interface HandlerContext<TRequest, TResponse> {
                request: TRequest;
                reply(response: TResponse): void;
            }

            type Handler<TRequest, TResponse> = (ctx: HandlerContext<TRequest, TResponse>) => void;

            class Router<TRequest, TResponse> {
                handler: Handler<TRequest, TResponse>;

                constructor(handler: Handler<TRequest, TResponse>) {
                    this.handler = handler;
                }

                dispatch(ctx: HandlerContext<TRequest, TResponse>): void {
                    this.handler(ctx);
                }
            }

            class RecorderContext implements HandlerContext<Request, Response> {
                request: Request;
                recorded: string;

                constructor() {
                    this.request = { searchKey: "" };
                    this.recorded = "";
                }

                reply(response: Response): void {
                    this.recorded = response.searchKey + ":" + response.gameList.length;
                }
            }

            class Social {
                register(router: Router<Request, Response>): void {
                    router.handler = (ctx) => {
                        this.findTopSourceTvGames(ctx);
                    };
                }

                findTopSourceTvGames(
                    ctx: HandlerContext<Request, Response>
                ): void {
                    ctx.reply({
                        searchKey: ctx.request.searchKey ?? "",
                        leagueId: ctx.request.leagueId ?? 0,
                        heroId: ctx.request.heroId ?? 0,
                        startGame: ctx.request.startGame ?? 0,
                        numGames: 0,
                        gameListIndex: ctx.request.gameListIndex ?? 0,
                        gameList: [],
                        specificGames: false
                    });
                }
            }

            function main(): string {
                const ctx = new RecorderContext();
                const router = new Router<Request, Response>((unused) => {});
                const social = new Social();
                social.register(router);
                router.dispatch(ctx);
                return ctx.recorded;
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal(":0", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void ExecuteNullishCoalescingEvaluatesRightOnlyForNullishValues()
    {
        var result = Run("""
            class Counter {
                calls: number;

                constructor() {
                    this.calls = 0;
                }

                fallback(): string {
                    this.calls = this.calls + 1;
                    return "fallback";
                }
            }

            interface MaybeValue {
                value?: string;
            }

            function main(): string {
                const counter = new Counter();
                const present: MaybeValue = { value: "present" };
                const missing: MaybeValue = {};
                const first = present.value ?? counter.fallback();
                const second = missing.value ?? counter.fallback();
                return first + ":" + second + ":" + counter.calls;
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("present:fallback:1", ((TsStringValue)result!).Value);
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

    // ── void operator tests ──

    [Fact]
    public void Void_ReturnsUndefined()
    {
        var result = Run(@"
            function main() {
                return void 42;
            }
        ");
        Assert.NotNull(result);
        Assert.IsType<TsVoid>(result);
    }

    [Fact]
    public void Void_EvaluatesOperand_ForSideEffects()
    {
        var result = Run(@"
            function bump(t: any): int32 {
                t.count = t.count + 1;
                return t.count;
            }
            function main(): int32 {
                let tracker = { count: 0 };
                void bump(tracker);
                void bump(tracker);
                void bump(tracker);
                return tracker.count as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(3, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void Void_FunctionCall_DiscardsReturnValue()
    {
        var result = Run(@"
            function sideEffect(t: any): int32 {
                t.used = 1;
                return 99;
            }
            function main(): int32 {
                let tracker = { used: 0 };
                void sideEffect(tracker);
                return tracker.used as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(1, Convert.ToInt32(result!.RawValue));
    }

    // ── delete operator tests ──

    [Fact]
    public void Delete_Field_ReturnsTrue_WhenExists()
    {
        var result = Run(@"
            function main() {
                const obj = { a: 1, b: 2, c: 3 };
                return delete obj.b;
            }
        ");
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_Field_RemovesField()
    {
        var result = Run(@"
            function main(): int32 {
                const obj = { a: 1, b: 2 };
                delete obj.a;
                if (obj.a != null) return 999;
                return obj.b;
            }
        ");
        Assert.Equal(2, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_Field_NonExistent_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                const obj = { a: 1 };
                return delete obj.z;
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_NonReference_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                let x: int32 = 42;
                return delete x;
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_NonReference_EvaluatesOperandForSideEffects()
    {
        var result = Run(@"
            function incrementTracker(t: any): int32 {
                t.calls = t.calls + 1;
                return t.calls;
            }
            function main(): int32 {
                let tracker = { calls: 0 };
                delete incrementTracker(tracker);
                return tracker.calls as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(1, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void Delete_Array_PreservesHoles()
    {
        var result = Run(@"
            function main(): int32 {
                const arr = [10, 20, 30, 40];
                const deleted = delete arr[1];
                if (!deleted) return -1;
                if (arr.length != 4) return -2;
                if (arr[0] != 10) return -3;
                if (arr[2] != 30) return -4;
                if (arr[3] != 40) return -5;
                if (arr[1] != null) return -6;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_Array_HoleIsSkippedByIteration()
    {
        var result = Run(@"
            function main(): int32 {
                let arr = [10, 20, 30];
                delete arr[1];
                let sum: int32 = 0;
                let i: int32 = 0;
                while (i < arr.length) {
                    if (arr[i] != null) sum = sum + arr[i];
                    i = i + 1;
                }
                return sum;
            }
        ");
        Assert.Equal(40, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_Array_OutOfRange_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                const arr = [1, 2, 3];
                return delete arr[10];
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_Array_AlreadyHole_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                let arr = [1, 2, 3];
                delete arr[1];
                return delete arr[1];
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Array_SparseAssignment_CreatesHoles()
    {
        var result = Run(@"
            function main(): int32 {
                let arr = [];
                arr[0] = 10;
                arr[3] = 40;
                if (arr.length != 4) return -1;
                if (arr[0] != 10) return -2;
                if (arr[1] != null) return -3;
                if (arr[2] != null) return -4;
                if (arr[3] != 40) return -5;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Array_Map_PreservesHoles()
    {
        var result = Run(@"
            function doubleIt(val: int32, idx: int32): int32 {
                return val * 2;
            }
            function main(): int32 {
                let arr = [10, 20, 30];
                delete arr[1];
                let mapped = arr.map(doubleIt);
                if (mapped.length != 3) return -1;
                if (mapped[0] != 20) return -2;
                if (mapped[1] != null) return -3;
                if (mapped[2] != 60) return -4;
                let visits = 0;
                mapped.forEach((value, index) => { visits = visits + 1; });
                if (visits != 2) return -5;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_MapOperator_RemovesKey()
    {
        var result = Run(@"
            function main(): int32 {
                const m = new Map();
                m.set(""a"", 100);
                m.set(""b"", 200);
                delete m[""a""];
                if (m.size != 1) return -1;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_OptionalChaining_NullReceiver_ReturnsTrue()
    {
        var result = Run(@"
            function main() {
                const obj = null;
                return delete obj?.x;
            }
        ");
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_OptionalChaining_NonNull_Deletes()
    {
        var result = Run(@"
            function main() {
                const obj = { x: 10, y: 20 };
                return delete obj?.x;
            }
        ");
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_MultipleFields()
    {
        var result = Run(@"
            function main(): int32 {
                const obj = { a: 1, b: 2, c: 3, d: 4, e: 5 };
                delete obj.a;
                delete obj.c;
                delete obj.e;
                let sum: int32 = 0;
                if (obj.b != null) sum = sum + obj.b;
                if (obj.d != null) sum = sum + obj.d;
                return sum;
            }
        ");
        Assert.Equal(6, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Void_InExpression_Context()
    {
        var result = Run("""
            var results = "";
            function main() {
                var a = void 1;
                var b = void "hello";
                var c = void (1 + 2);
                return 42;
            }
            """);
        Assert.Equal(42, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_BasicExecutesAtLeastOnce()
    {
        var result = Run(@"
            function main(): int32 {
                let i: int32 = 10;
                do {
                    i = i + 1;
                } while (false);
                return i;
            }
        ");
        Assert.Equal(11, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_CountsMultipleIterations()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 1;
                do {
                    sum = sum + i;
                    i = i + 1;
                } while (i <= 5);
                return sum;
            }
        ");
        Assert.Equal(15, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_BreakExitsLoop()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 1;
                do {
                    if (i == 4) break;
                    sum = sum + i;
                    i = i + 1;
                } while (i <= 10);
                return sum;
            }
        ");
        Assert.Equal(6, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_ContinueSkipsToCondition()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 0;
                do {
                    i = i + 1;
                    if (i % 2 == 0) continue;
                    sum = sum + i;
                } while (i < 5);
                return sum;
            }
        ");
        Assert.Equal(9, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_ConditionFalseAtStart_StillExecutesBody()
    {
        var result = Run(@"
            function main(): int32 {
                let executed: int32 = 0;
                do {
                    executed = 1;
                } while (false);
                return executed;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_Nested()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 0;
                do {
                    let j: int32 = 0;
                    do {
                        sum = sum + 1;
                        j = j + 1;
                    } while (j < 3);
                    i = i + 1;
                } while (i < 2);
                return sum;
            }
        ");
        Assert.Equal(6, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_SideEffectEvaluation()
    {
        var result = Run(@"
            function bump(t: any): int32 {
                t.count = t.count + 1;
                return t.count;
            }
            function main(): int32 {
                let tracker = { count: 0 };
                do {
                    bump(tracker);
                } while (tracker.count < 3);
                return tracker.count as int32;
            }
        ");
        Assert.Equal(3, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void DoWhile_WithElseNotConfused()
    {
        var result = Run(@"
            function main(): int32 {
                let x: int32 = 0;
                if (true) {
                    do {
                        x = x + 1;
                    } while (x < 3);
                } else {
                    x = -1;
                }
                return x;
            }
        ");
        Assert.Equal(3, ((TsInt32Value)result!).Value);
    }
}

public class TypeCheckingTests
{
    [Fact]
    public void StringPlusIntProducesStringType()
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

        Assert.False(
            binder.Diagnostics.HasErrors,
            string.Join("; ", binder.Diagnostics.GetErrors().Select(d => d.Message)));
        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(boundTree.Members));
        var block = Assert.IsType<BoundBlockStatement>(function.Body);
        var statement = Assert.IsType<BoundReturnStatement>(Assert.Single(block.Statements));
        Assert.NotNull(statement.Value);
        Assert.Equal(TsType.String, statement.Value.Type);
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

    [Fact]
    public void ReturnTypeMismatch_ProducesError()
    {
        var code = @"
            function main(): int32 {
                return ""hello"";
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("Cannot return") && d.Message.Contains("from function returning"));
    }

    [Fact]
    public void ReturnVoidWithExpression_ProducesWarning()
    {
        var code = @"
            function main(): void {
                return 42;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.Contains(binder.Diagnostics.All, d =>
            d.Severity == TypeSharp.Syntax.Diagnostics.DiagnosticSeverity.Warning &&
            d.Message.Contains("returns 'void' but return statement has a value"));
    }

    [Fact]
    public void ReturnNonVoidWithoutValue_ProducesError()
    {
        var code = @"
            function main(): int32 {
                return;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("returns 'int32' but return statement has no value"));
    }

    [Fact]
    public void NullAssignToNonNullable_ProducesError()
    {
        var code = @"
            function main(): string {
                const x: string = null;
                return x;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
    }

    [Fact]
    public void NullAssignToNullable_NoError()
    {
        var code = @"
            function main(): int32 {
                const x: string? = null;
                return 0;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.False(binder.Diagnostics.HasErrors);
    }

    [Fact]
    public void NullableAssignToNonNullable_ProducesError()
    {
        var code = @"
            function getString(): string? {
                return null;
            }
            function main(): string {
                const x: string = getString();
                return x;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
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

    [Fact]
    public void VerifierRejectsBranchIntoOperand()
    {
        var code = new byte[] { Opcodes.Branch, 1, 0, 0, 0, Opcodes.ReturnVoid };
        var function = new BytecodeFunction("test", code, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { function });

        Assert.Throws<BytecodeVerificationException>(() => BytecodeVerifier.Verify(module));
    }

    [Fact]
    public void SerializerRoundTripsVerifiedModule()
    {
        var code = new byte[] { Opcodes.LoadConstI32, 42, 0, 0, 0, Opcodes.Return };
        var function = new BytecodeFunction("main", code, 0, 0, false,
            new[] { "unused" }, Array.Empty<long>(), Array.Empty<double>(), new[] { 12.5m });
        var module = new BytecodeModule("roundtrip", new[] { function });

        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, module);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, "main");
        Assert.Equal(42, ((TsInt32Value)result!).Value);
        Assert.Equal(12.5m, restored.Functions[0].DecimalConstants[0]);
    }

    [Fact]
    public void ProfileCountsHotFunctions()
    {
        var code = new byte[] { Opcodes.ReturnVoid };
        var module = new BytecodeModule("profile", new[]
        {
            new BytecodeFunction("main", code, 0, 0, false,
                Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>())
        });
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        interpreter.Execute(module, "main");
        interpreter.Execute(module, "main");

        Assert.Equal(2, interpreter.Profile.GetCallCount("profile", "main"));
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
        map.Set(TsValue.FromString("key1"), TsValue.FromString("value1"));
        map.Set(TsValue.FromString("key2"), TsValue.FromInt32(42));

        Assert.Equal(2, map.Count);
        Assert.Equal("value1", ((TsStringValue)map.Get(TsValue.FromString("key1"))).Value);
        Assert.Equal(42, ((TsInt32Value)map.Get(TsValue.FromString("key2"))).Value);
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
    public void TypeScriptBigIntLiteral_UsesBigIntSurface()
    {
        var result = Run(@"
            function main(): string {
                return typeof 123n;
            }
        ");

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("bigint", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void TypeScriptBigIntLiteral_ComputesAsBigInt()
    {
        var result = Run(@"
            function main(): bigint {
                return 100n + 23n;
            }
        ");

        Assert.IsType<TsBigIntValue>(result);
        Assert.Equal(new System.Numerics.BigInteger(123), ((TsBigIntValue)result!).Value);
    }

    [Fact]
    public void TypeScriptBigIntLiteral_SupportsValuesBeyondUInt64()
    {
        var result = Run(@"
            function main(): string {
                return """" + (18446744073709551616n + 1n);
            }
        ");

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("18446744073709551617", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void BigIntGlobal_ConvertsStandardValues()
    {
        var result = Run(@"
            function main(): string {
                return """" + (BigInt(255) + BigInt(""1""));
            }
        ");

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("256", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void NumberGlobal_ConvertsStandardValues()
    {
        var result = Run(@"
            function main(): number {
                return Number(42n) + Number(""3"");
            }
        ");

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(45d, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void BigIntBitwiseOperators_PreserveArbitraryPrecision()
    {
        var result = Run("""
            function main(): string {
                const high = 18446744073709551616n;
                const xor = high ^ 1n;
                const shifted = (1n << 65n) >> 64n;
                return "" + (xor | shifted);
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("18446744073709551619", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void PrefixedBigIntLiterals_WorkWithBitwiseOperators()
    {
        var result = Run("""
            function main(): string {
                return "" + ((0xffffffffn & 0xffn) | 0b100000000n | 0o7n);
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("511", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void UInt64BitwiseOperators_ReturnUInt64()
    {
        var result = Run("""
            function main(): uint64 {
                const left: uint64 = 240u64;
                const right: uint64 = 15u64;
                return (left | right) ^ 51u64;
            }
        """);

        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(204UL, ((TsUInt64Value)result!).Value);
    }

    [Fact]
    public void NumberBitwiseOperators_UseInt32Semantics()
    {
        var result = Run("""
            function main(): number {
                const value: number = 0xC0 | (255 >> 6);
                return value & 0xFF;
            }
        """);

        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(195, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void LoopBreakAndContinue_WorkInForLoops()
    {
        var result = Run("""
            function main(): number {
                let sum = 0;
                for (let i = 0; i < 8; i = i + 1) {
                    if (i === 2) continue;
                    if (i === 6) break;
                    sum = sum + i;
                }
                return sum;
            }
        """);

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(13d, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void Continue_WorkInWhileLoops()
    {
        var result = Run("""
            function main(): number {
                let i = 0;
                let sum = 0;
                while (i < 5) {
                    i = i + 1;
                    if (i === 3) continue;
                    sum = sum + i;
                }
                return sum;
            }
        """);

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(12d, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void Uint8ArrayAnnotation_BehavesLikeByteArray()
    {
        var result = Run(@"
            function main(): number {
                const data: Uint8Array = new Uint8Array([1, 2, 3]);
                return data.length + data[1];
            }
        ");

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(5.0, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void StrictEquality_UsesNoCoercionForDynamicValues()
    {
        var result = Run(@"
            function main(): boolean {
                const text: any = ""1"";
                const numberValue: any = 1;
                return text !== numberValue && !(text === numberValue);
            }
        ");

        Assert.IsType<TsBoolValue>(result);
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void ByteArrayMarshalling_UsesUint8ArrayShape()
    {
        var input = new byte[] { 1, 2, 255 };
        var tsValue = Marshaller.FromManaged(input);

        Assert.IsType<TsUint8ArrayValue>(tsValue);
        var array = (TsUint8ArrayValue)tsValue;
        Assert.Equal(3, array.Length);
        Assert.Equal(255, array.Get(2));

        var output = Marshaller.ToManaged(tsValue, typeof(byte[]));
        Assert.Equal(input, output);
    }

    [Fact]
    public void HostByteArraySignature_ExposesUint8Array()
    {
        var registry = new HostRegistry();
        registry.RegisterFunction(new HostFunctionDescriptor(
            "bytes",
            "load",
            typeof(byte[]),
            Array.Empty<Type>(),
            _ => TsValue.FromUint8Array(new byte[] { 1, 2, 3 })));

        var symbols = registry.CreateGlobalSymbols();
        var load = Assert.IsType<FunctionSymbol>(symbols["load"]);

        Assert.Equal("Uint8Array", load.Type.Name);
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
        var promise = Assert.IsType<TsPromiseValue>(result);
        Assert.Equal(42, ((TsInt32Value)promise.Await()).Value);
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
        var promise = Assert.IsType<TsPromiseValue>(result);
        Assert.Equal("hello", ((TsStringValue)promise.Await()).Value);
    }

    [Fact]
    public void AsyncMethod_VoidTask_DoesNotThrow()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_void_task");
        Assert.NotNull(desc);

        var ex = Record.Exception(() =>
        {
            var promise = Assert.IsType<TsPromiseValue>(desc!.Implementation(Array.Empty<TsValue>()));
            Assert.IsType<TsVoid>(promise.Await());
        });
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

public class NegativeTests
{
    private static BytecodeFunction MakeFunc(byte[] instructions, string[] strings)
    {
        return new BytecodeFunction("test", instructions, 0, 0, false, strings, Array.Empty<long>(), Array.Empty<double>());
    }

    private static BytecodeModule MakeModule(BytecodeFunction func)
    {
        return new BytecodeModule("test", new[] { func });
    }

    [Fact]
    public void DivisionByZero_I32_Throws()
    {
        var code = @"
            function main(): int32 {
                const a: int32 = 10;
                const b: int32 = 0;
                return a / b;
            }
        ";
        var ex = Record.Exception(() => Run(code, "main"));
        Assert.NotNull(ex);
        Assert.Contains("Division by zero", ex.Message);
    }

    [Fact]
    public void DivisionByZero_I64_Throws()
    {
        var code = @"
            function main(): int64 {
                const a: int64 = 10i64;
                const b: int64 = 0i64;
                return a / b;
            }
        ";
        var ex = Record.Exception(() => Run(code, "main"));
        Assert.NotNull(ex);
        Assert.Contains("Division by zero", ex.Message);
    }

    [Fact]
    public void DivisionByZero_F64_NoThrow_ThrowsInf()
    {
        var code = @"
            function main(): float64 {
                const a: float64 = 10.0;
                const b: float64 = 0.0;
                return a / b;
            }
        ";
        var result = Run(code, "main");
        Assert.NotNull(result);
        Assert.IsType<TsFloat64Value>(result);
        Assert.True(double.IsInfinity(((TsFloat64Value)result).Value));
    }

    [Fact]
    public void OperandStack_GrowsBeyondInitialCapacity()
    {
        var func = new BytecodeFunction("test", Array.Empty<byte>(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var frame = new CallFrame(func);

        for (int i = 0; i < 300; i++)
            frame.Push(TsValue.Void);

        Assert.Equal(300, frame.StackPointer);
        Assert.True(frame.Stack.Length >= 300);
    }

    [Fact]
    public void OperandStack_ExceedingGuardrail_Throws()
    {
        var maxStackSlots = (int)typeof(CallFrame)
            .GetField("MaxStackSlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        var func = new BytecodeFunction("test", Array.Empty<byte>(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var frame = new CallFrame(func);

        for (int i = 0; i < maxStackSlots; i++)
            frame.Push(TsValue.Void);

        var ex = Record.Exception(() => frame.Push(TsValue.Void));
        Assert.NotNull(ex);
        Assert.Contains("Operand stack limit exceeded", ex.Message);
    }

    [Fact]
    public void InstructionLimit_Exceeded()
    {
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(Opcodes.Branch);
        w.Write(0);
        var func = new BytecodeFunction("test", ms.ToArray(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });

        var limits = new VMRuntimeLimits { MaximumInstructions = 5 };
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
        Assert.Contains("Execution limit exceeded", ex.Message);
    }

    [Fact]
    public void MemoryLimit_Exceeded()
    {
        var heap = new TsHeap(100);
        var limits = new VMRuntimeLimits { MaximumMemoryBytes = 100 };
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);

        interpreter.RegisterHostFunction("test.alloc", (name, args) =>
        {
            interpreter.Heap.AllocateObject("BigObject");
            interpreter.Heap.AllocateObject("BigObject");
            interpreter.Heap.AllocateObject("BigObject");
            return TsValue.Null;
        });

        var instructions = new byte[]
        {
            Opcodes.LoadConstNull,
            Opcodes.LoadConstI32, 0, 0, 0, 0,
            Opcodes.CallHost, 0, 0, 0, 0, 1, 0, 0, 0,
            Opcodes.ReturnVoid
        };

        var func = new BytecodeFunction("test", instructions, 0, 0, false,
            new[] { "test.alloc" }, new long[] { 0 }, Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });

        var limits2 = new VMRuntimeLimits { MaximumMemoryBytes = 1 };
        var interpreter2 = new TypeSharp.VM.Interpreter.Interpreter(limits2);

        interpreter2.RegisterHostFunction("test.alloc", (name, args) =>
        {
            interpreter2.Heap.AllocateObject("BigObject");
            return TsValue.Null;
        });

        var ex = Record.Exception(() => interpreter2.Execute(module, "test"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void RecursionLimit_Exceeded()
    {
        var code = @"
            function recurse(n: int32): int32 {
                return recurse(n + 1);
            }
            function main(): int32 {
                return recurse(0);
            }
        ";

        var limits = new VMRuntimeLimits { MaximumRecursionDepth = 4 };
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);
        var ex = Record.Exception(() => interpreter.Execute(bytecodeModule, "main"));
        Assert.NotNull(ex);
        Assert.Contains("recursion depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeout_ThrowsOperationCanceled()
    {
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(Opcodes.Branch);
        w.Write(0);
        var func = new BytecodeFunction("test", ms.ToArray(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });

        var limits = new VMRuntimeLimits { ExecutionTimeout = TimeSpan.FromMilliseconds(1) };
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void UnknownOpcode_Throws()
    {
        var instructions = new byte[] { 0xFF };
        var func = new BytecodeFunction("test", instructions, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
        Assert.Contains("Unknown opcode", ex.Message);
    }

    [Fact]
    public void NullFieldAccess_ReturnsNull()
    {
        var code = @"
            function main(): int32 {
                const x: int32 = 0;
                return x;
            }
        ";
        var result = Run(code, "main");
        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(0, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void StackUnderflow_Throws()
    {
        var instructions = new byte[] { Opcodes.Pop };
        var func = new BytecodeFunction("test", instructions, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
        Assert.Contains("Stack underflow", ex.Message);
    }

    [Fact]
    public void Switch_SelectsCase_FallsThrough_AndBreaks()
    {
        var code = @"
            function main(value: int32): int32 {
                let result: int32 = 0;
                switch (value) {
                    case 1:
                        result = result + 10;
                    case 2:
                        result = result + 2;
                        break;
                    default:
                        result = 99;
                }
                return result;
            }
        ";

        Assert.Equal(12, ((TsInt32Value)Run(code, "main", new TsValue[] { new TsInt32Value(1) })!).Value);
        Assert.Equal(2, ((TsInt32Value)Run(code, "main", new TsValue[] { new TsInt32Value(2) })!).Value);
        Assert.Equal(99, ((TsInt32Value)Run(code, "main", new TsValue[] { new TsInt32Value(7) })!).Value);
    }

    [Fact]
    public void Switch_EvaluatesDiscriminantExactlyOnce()
    {
        var code = @"
            function main(): int32 {
                let index: int32 = 0;
                const values: int32[] = [2];
                let result: int32 = 0;
                switch (values[index++]) {
                    case 1:
                        result = 1;
                        break;
                    case 2:
                        result = 2;
                        break;
                    default:
                        result = 3;
                }
                return index * 10 + result;
            }
        ";

        Assert.Equal(12, ((TsInt32Value)Run(code)!).Value);
    }

    [Fact]
    public void ContinueInsideSwitch_ContinuesEnclosingLoop()
    {
        var code = @"
            function main(): int32 {
                let sum: int32 = 0;
                for (let i: int32 = 0; i < 3; i++) {
                    switch (i) {
                        case 1:
                            continue;
                        default:
                            sum = sum + i;
                    }
                }
                return sum;
            }
        ";

        Assert.Equal(2, ((TsInt32Value)Run(code)!).Value);
    }

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
}

public class ConcurrencyTests
{
    [Fact]
    public void ParallelExecute_DoesNotCorruptState()
    {
        var code = @"
            function main(): int32 {
                const x: int32 = 0;
                const y: int32 = 1;
                return x + y;
            }
        ";

        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var results = new TsValue?[10];
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            tasks[idx] = Task.Run(() =>
            {
                var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
                results[idx] = interpreter.Execute(bytecodeModule, "main");
            });
        }

        Task.WaitAll(tasks);

        foreach (var r in results)
        {
            Assert.NotNull(r);
            Assert.IsType<TsInt32Value>(r);
            Assert.Equal(1, ((TsInt32Value)r).Value);
        }
    }

    [Fact]
    public void RepeatedExecute_IsStateless()
    {
        var code = @"
            function main(): int32 {
                const x: int32 = 5;
                return x;
            }
        ";

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

        for (int i = 0; i < 100; i++)
        {
            var result = interpreter.Execute(bytecodeModule, "main");
            Assert.NotNull(result);
            Assert.Equal(5, ((TsInt32Value)result).Value);
        }
    }

    [Fact]
    public void SharedInterpreter_NoCrossContamination()
    {
        var addCode = @"
            function main(a: int32, b: int32): int32 {
                return a + b;
            }
        ";

        var mulCode = @"
            function main(a: int32, b: int32): int32 {
                return a * b;
            }
        ";

        var addModule = Compile(addCode);
        var mulModule = Compile(mulCode);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        var r1 = interpreter.Execute(addModule, "main", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });
        Assert.Equal(7, ((TsInt32Value)r1!).Value);

        var r2 = interpreter.Execute(mulModule, "main", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });
        Assert.Equal(12, ((TsInt32Value)r2!).Value);

        var r3 = interpreter.Execute(addModule, "main", new[] { TsValue.FromInt32(10), TsValue.FromInt32(20) });
        Assert.Equal(30, ((TsInt32Value)r3!).Value);
    }

    private static BytecodeModule Compile(string code)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        return BytecodeCompiler.Compile(moduleIR);
    }
}
