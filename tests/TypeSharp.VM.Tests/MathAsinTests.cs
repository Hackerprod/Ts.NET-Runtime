using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class MathAsinTests
{
    private const string Target = "Math::asin";
    private readonly ITestOutputHelper _output;

    public MathAsinTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Asin_ReturnsPrincipalAngleInRadians()
    {
        var witness = Compile("w", "c168-math-asin.ts",
            "function main(x: number): any { return Math.asin(x); }", TsType.Any, 1);
        CheckRow(witness, "w-positive-zero", 0.0, 0.0);
        var typed = Compile("t", "c168-math-asin-typed.ts",
            "function main(x: number): number { return Math.asin(x); }", TsType.Number, 1);
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        CheckRow(typed, "negative-one", -1.0, -1.5707963267948966);
        CheckRow(typed, "negative-half", -0.5, -0.5235987755982989);
        CheckRow(typed, "positive-half", 0.5, 0.5235987755982989);
        CheckRow(typed, "positive-one", 1.0, 1.5707963267948966);
        CheckRow(typed, "positive-zero", 0.0, 0.0);
        CheckRow(typed, "negative-zero", negativeZero, negativeZero);
        CheckRow(typed, "outside", 1.5, double.NaN);
        CheckRow(typed, "nan", double.NaN, double.NaN);
        var omitted = Compile("o", "c168-math-asin-omitted.ts",
            "function main(): number { return Math.asin(); }", TsType.Number, 0);
        CheckRow(omitted, "omitted", null, double.NaN);

        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        var math = Assert.Single(Regex.Matches(File.ReadAllText(path), @"(?ms)^interface Math[ \t]*\{\s*?(.*?)^\}"));
        var signature = Assert.Single(math.Groups[1].Value.Split('\n').Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, @"^asin\b")));
        Assert.Equal("asin(x: number): number;", signature);
        _output.WriteLine($"C168 MathAsin stage=declaration signature={signature}");
    }

    private BytecodeModule Compile(string label, string sourceName, string source, TsType returnType, int argc)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, sourceName).Tokenize());
        var tree = parser.Parse();
        _output.WriteLine(FormattableString.Invariant($"C168 MathAsin source={label} stage=parse count={parser.Diagnostics.Count}"));
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var diagnostics = binder.Diagnostics.All;
        _output.WriteLine(FormattableString.Invariant($"C168 MathAsin source={label} stage=bind count={diagnostics.Count}"));
        foreach (var item in diagnostics)
        {
            var loc = item.Location;
            _output.WriteLine(FormattableString.Invariant(
                $"C168 MathAsin source={label} stage=bind-diagnostic severity={item.Severity} code={item.Code} numeric-code={(int)item.Code} file={loc.Source} line={loc.Line} column={loc.Column} offset={loc.Offset} message={item.Message}"));
        }
        Assert.Empty(diagnostics);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(returnType, main.Symbol.Type);
        if (argc == 0)
            Assert.Empty(main.Symbol.Parameters);
        else
            Assert.Same(TsType.Number, Assert.Single(main.Symbol.Parameters).Type);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var ns = Assert.IsType<BoundVariableExpression>(access.Object);
        var math = Assert.IsType<ClassSymbol>(ns.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("asin", method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        Assert.Same(TsType.Number, call.Type);
        if (argc == 0)
            Assert.Empty(call.Arguments);
        else
            Assert.Same(TsType.Number, Assert.Single(call.Arguments).Type);

        var ir = new IRGenerator().Generate(bound);
        var fn = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = Assert.Single(fn.Blocks).Instructions;
        var callIndex = argc == 0 ? 0 : 1;
        if (argc == 0)
            Assert.Equal(new[] { Opcode.Call, Opcode.Return }, ops.Select(op => op.Opcode));
        else
        {
            Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }, ops.Select(op => op.Opcode));
            Assert.Equal(0, ops[0].Operand0);
        }
        Assert.Equal(Target, Assert.IsType<string>(ops[callIndex].OperandObject));
        Assert.Equal(argc, ops[callIndex].Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(compiled.Functions, function => function.Name == "main");
        var bytes = encoded.Instructions;
        var callOffset = argc == 0 ? 0 : 5;
        Assert.Equal(callOffset + 10, bytes.Length);
        if (argc != 0)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[0]);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4)));
        }
        Assert.Equal(Opcodes.Call, bytes[callOffset]);
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 1, 4));
        Assert.InRange(index, 0, encoded.StringConstants.Length - 1);
        var actualTarget = encoded.StringConstants[index];
        Assert.Equal(Target, actualTarget);
        var actualArgc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 5, 4));
        Assert.Equal(argc, actualArgc);
        Assert.Equal(Opcodes.Return, bytes[callOffset + 9]);
        _output.WriteLine(FormattableString.Invariant(
            $"C168 MathAsin source={label} stage=compiled target={actualTarget} argc={actualArgc}"));
        return compiled;
    }

    private void CheckRow(BytecodeModule module, string label, double? input, double expected)
    {
        var args = Array.Empty<TsValue>();
        if (input is double number)
        {
            var wrapped = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(number));
            Assert.Equal(TsValueType.Float64, wrapped.ValueType);
            args = new TsValue[] { wrapped };
        }
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsFloat64Value>(interpreter.Execute(module, "main", args));
        Assert.Equal(TsValueType.Float64, actual.ValueType);
        string observed;
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual.Value));
            observed = "NaN";
        }
        else if (expected == 0.0)
        {
            var actualBits = BitConverter.DoubleToInt64Bits(actual.Value);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), actualBits);
            observed = actualBits.ToString("x16", CultureInfo.InvariantCulture);
        }
        else
        {
            Assert.True(double.IsFinite(actual.Value));
            Assert.InRange(Math.Abs(actual.Value - expected), 0.0, 1e-12);
            observed = actual.Value.ToString("R", CultureInfo.InvariantCulture);
        }
        _output.WriteLine($"C168 MathAsin row={label} type={actual.ValueType} actual={observed}");
    }
}
