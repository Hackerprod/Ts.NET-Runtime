using System.Buffers.Binary;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class NumberNamespacePrimitiveTests
{
    private const string SignedBigIntExpression = "Number(BigInt('-9007199254740995'))";

    [Theory]
    [InlineData("Number()", "Number", 0, "0000000000000000")]
    [InlineData("Number(undefined)", "Number", 1, "NaN")]
    [InlineData("Number('0x2a')", "Number", 1, "4045000000000000")]
    [InlineData("Number('-0')", "Number", 1, "8000000000000000")]
    [InlineData("Number(9007199254740995n)", "Number", 1, "4340000000000002")]
    [InlineData(SignedBigIntExpression, "Number", 1, "c340000000000002")]
    [InlineData("Number.isFinite()", "Number::isFinite", 0, "false")]
    [InlineData("Number.isInteger(42, 'ignored')", "Number::isInteger", 2, "true")]
    [InlineData("Number.isNaN('NaN', 1n)", "Number::isNaN", 2, "false")]
    [InlineData("Number.isSafeInteger(9007199254740991.0)", "Number::isSafeInteger", 1, "true")]
    [InlineData("Number.parseFloat()", "Number::parseFloat", 0, "NaN")]
    [InlineData("Number.parseInt()", "Number::parseInt", 0, "NaN")]
    [InlineData("Number.parseFloat(-0.0)", "Number::parseFloat", 1, "0000000000000000")]
    [InlineData("Number.parseFloat('-0')", "Number::parseFloat", 1, "8000000000000000")]
    [InlineData("Number.parseInt('-0')", "Number::parseInt", 1, "8000000000000000")]
    [InlineData("Number.parseInt(1e20)", "Number::parseInt", 1, "4415af1d78b58c40")]
    [InlineData("Number.parseInt(1e21, 16)", "Number::parseInt", 2, "403e000000000000")]
    [InlineData("Number.parseInt(1e-6)", "Number::parseInt", 1, "0000000000000000")]
    [InlineData("Number.parseInt(5e-324, 36)", "Number::parseInt", 2, "4068400000000000")]
    [InlineData("Number.parseInt('101', '0x100000002')", "Number::parseInt", 2, "4014000000000000")]
    [InlineData("Number.parseInt('101', -4294967294.0)", "Number::parseInt", 2, "4014000000000000")]
    [InlineData("Number.parseInt('123', '2')", "Number::parseInt", 2, "3ff0000000000000")]
    [InlineData("parseInt('123', '2')", "parseInt", 2, "405ec00000000000")]
    public void DirectPrimitiveCallsCarryActualArity(string expression, string target, int argc, string expected)
    {
        var ir = Compile($"function main(): any {{ return {expression}; }}");
        var calls = Instructions(ir).Where(i => i.Opcode == Opcode.Call).ToArray();
        if (expression == SignedBigIntExpression)
        {
            // Construct the signed carrier before dispatch: unary minus on a BigInt
            // literal currently takes the separate legacy Neg_F64 lowering path.
            Assert.Collection(calls, c => CheckCall(c, "BigInt", 1), c => CheckCall(c, target, argc));
        }
        else
        {
            CheckCall(Assert.Single(calls), target, argc);
        }
        CheckResult(expected, Execute(ir));

        static void CheckCall(Instruction call, string name, int count)
        {
            Assert.Equal(name, Assert.IsType<string>(call.OperandObject));
            Assert.Equal(count, call.Operand1);
        }
    }

    [Theory]
    [InlineData("EPSILON", "3cb0000000000000")]
    [InlineData("MIN_VALUE", "0000000000000001")]
    [InlineData("MAX_VALUE", "7fefffffffffffff")]
    [InlineData("MIN_SAFE_INTEGER", "c33fffffffffffff")]
    [InlineData("MAX_SAFE_INTEGER", "433fffffffffffff")]
    [InlineData("POSITIVE_INFINITY", "7ff0000000000000")]
    [InlineData("NEGATIVE_INFINITY", "fff0000000000000")]
    public void ConstantsSurviveTheWireAsExactFloat64(string member, string expected)
    {
        var ir = Compile($"function main(): any {{ return Number.{member}; }}");
        var ins = Instructions(ir).ToArray();
        Assert.Equal(2, ins.Length);
        Assert.Equal(Opcode.LoadConst_F64, ins[0].Opcode);
        var decoded = Decode(ir);
        var fn = Assert.Single(decoded.Functions, f => f.Name == "main");
        Assert.Equal(Opcodes.LoadConstF64, fn.Instructions[0]);
        Assert.Equal(expected, $"{BinaryPrimitives.ReadInt64LittleEndian(fn.Instructions.AsSpan(1)):x16}");
        CheckResult(expected, new Vm().Execute(decoded, "main"));
    }

    [Theory]
    [InlineData("Number.EPSILON = 1.0; return 0;", "readonly member 'EPSILON'")]
    [InlineData("Number.EPSILON += 1.0; return 0;", "readonly member 'EPSILON'")]
    [InlineData("return Number.EPSILON++;", "readonly member 'EPSILON'")]
    [InlineData("return ++Number.EPSILON;", "readonly member 'EPSILON'")]
    [InlineData("return Number.EPSILON();", "constant 'EPSILON' is not callable")]
    public void NumberConstantIsNotAWriteOrCallTarget(string body, string expectedDiagnostic)
    {
        var (binder, _) = Bind("function main(): any { " + body + " }");
        Assert.Contains(binder.Diagnostics.All, d => d.Message.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void UserClassNumberUsesItsOwnStaticStorage()
    {
        var ir = Compile("class Number { static EPSILON: number = 100; } function main(): any { return Number.EPSILON; }");
        Assert.Contains(Instructions(ir), i => i.Opcode == Opcode.LoadGlobal);
        Assert.Equal(100, Assert.IsType<TsInt32Value>(Execute(ir)).Value);
    }

    [Fact]
    public void CallableAliasUsesTheSameNumberConversion()
    {
        var ir = Compile("function main(): any { const n = Number; return n('0x2a'); }");
        var instructions = Instructions(ir).ToArray();
        var load = Assert.Single(instructions, i => i.Opcode == Opcode.LoadFunc);
        Assert.Equal("Number", Assert.IsType<string>(load.OperandObject));
        var call = Assert.Single(instructions, i => i.Opcode == Opcode.CallDynamic);
        Assert.Equal(1, call.Operand0);
        CheckResult("4045000000000000", Execute(ir));
    }

    [Fact]
    public void ThrowingExtraIsEvaluatedBeforeBigIntRadixConversion()
    {
        string pre = "function extra(): any { throw 'extra'; } ";
        string body = "export function main(): any { try { return Number.parseInt('', 2n, extra()); } catch(e) { return e; } }";
        Assert.Equal("extra", Assert.IsType<TsStringValue>(Execute(Compile(pre + body))).Value);
    }

    [Fact]
    public void BigIntRadixThrowsEvenForAnEmptyPrefix()
    {
        const string source = "function main(): any { try { return Number.parseInt('', 2n); } catch(e) { return [(e as any).name, (e as any).message]; } }";
        var ir = Compile(source);
        var call = Assert.Single(Instructions(ir), i => i.Opcode == Opcode.Call);
        Assert.Equal("Number::parseInt", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var error = Assert.IsType<TsArrayValue>(Execute(ir)).Value;
        Assert.Equal(2, error.Count);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(error.Get(0)).Value);
        Assert.Equal("Cannot convert a BigInt value to a number",
            Assert.IsType<TsStringValue>(error.Get(1)).Value);
    }

    [Fact]
    public void TemplateDeclaresExactlyTheSupportedNumberMembers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        string declaration = File.ReadAllText(path);
        const string header = "interface NumberConstructor {";
        int start = declaration.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0 && declaration.LastIndexOf(header, StringComparison.Ordinal) == start);
        start += header.Length;
        string[] members = declaration[start..declaration.IndexOf('}', start)].Split(';',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] expected =
        [
            "(value?: unknown, ...ignored: unknown[]): number",
            "readonly MAX_SAFE_INTEGER: number", "readonly MIN_SAFE_INTEGER: number",
            "readonly MAX_VALUE: number", "readonly MIN_VALUE: number", "readonly EPSILON: number",
            "readonly POSITIVE_INFINITY: number", "readonly NEGATIVE_INFINITY: number",
            "isFinite(value?: unknown, ...ignored: unknown[]): boolean",
            "isNaN(value?: unknown, ...ignored: unknown[]): boolean",
            "isInteger(value?: unknown, ...ignored: unknown[]): boolean",
            "isSafeInteger(value?: unknown, ...ignored: unknown[]): boolean",
            "parseInt(value?: unknown, radix?: unknown, ...ignored: unknown[]): number",
            "parseFloat(value?: unknown, ...ignored: unknown[]): number"
        ];
        Assert.Equal(expected, members);
    }

    private static (Binder Binder, BoundSourceFile Bound) Bind(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Error);
        var parser = new Parser(tokens);
        var ast = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        return (binder, binder.Bind(ast));
    }

    private static ModuleIR Compile(string source)
    {
        var (binder, bound) = Bind(source);
        Assert.Empty(binder.Diagnostics.All);
        return new IRGenerator().Generate(bound);
    }

    private static IEnumerable<Instruction> Instructions(ModuleIR ir) =>
        Assert.Single(ir.Functions, f => f.Name == "main").Blocks.SelectMany(b => b.Instructions);

    private static BytecodeModule Decode(ModuleIR ir)
    {
        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);
        using var wire = new MemoryStream();
        BytecodeSerializer.Serialize(wire, module);
        wire.Position = 0;
        var decoded = BytecodeSerializer.Deserialize(wire);
        BytecodeVerifier.Verify(decoded);
        Assert.Equal(wire.Length, wire.Position);
        return decoded;
    }

    private static TsValue? Execute(ModuleIR ir) => new Vm().Execute(Decode(ir), "main");

    private static void CheckResult(string expected, TsValue? actual)
    {
        if (expected is "false" or "true")
            Assert.Equal(expected == "true", Assert.IsType<TsBoolValue>(actual).Value);
        else
        {
            double value = Assert.IsType<TsFloat64Value>(actual).Value;
            if (expected == "NaN") Assert.True(double.IsNaN(value));
            else Assert.Equal(expected, $"{BitConverter.DoubleToInt64Bits(value):x16}");
        }
    }
}
