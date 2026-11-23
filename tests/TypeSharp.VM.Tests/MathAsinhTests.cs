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

public class MathAsinhTests(ITestOutputHelper output)
{
    private const string Target = "Math::asinh";
    private const double One = 0.881373587019543;

    [Fact]
    public void Asinh_ReturnsInverseHyperbolicSine()
    {
        var witness = Compile("w", "c171-math-asinh.ts",
            "function main(x: number): any { return Math.asinh(x); }", TsType.Any, 1);
        AssertPart(witness, "w-positive-one", One, 1.0);

        var typed = Compile("t", "c171-math-asinh-typed.ts",
            "function main(x: number): number { return Math.asinh(x); }", TsType.Number, 1);
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        AssertPart(typed, "positive-zero", 0.0, 0.0);
        AssertPart(typed, "negative-zero", negativeZero, negativeZero);
        AssertPart(typed, "positive-one", One, 1.0);
        AssertPart(typed, "negative-one", -One, -1.0);
        AssertPart(typed, "positive-infinity", double.PositiveInfinity, double.PositiveInfinity);
        AssertPart(typed, "negative-infinity", double.NegativeInfinity, double.NegativeInfinity);
        AssertPart(typed, "nan", double.NaN, double.NaN);
        AssertPart(typed, "large-positive", 0.0, 1e300);
        AssertPart(typed, "tiny-positive", 1e-300, 1e-300);
        var omitted = Compile("o", "c171-math-asinh-omitted.ts",
            "function main(): number { return Math.asinh(); }", TsType.Number, 0);
        AssertPart(omitted, "omitted", double.NaN);

        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var declarations = File.ReadAllText(Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var math = Assert.Single(Regex.Matches(declarations, @"(?ms)^interface Math[ \t]*\{(.*?)^\}"));
        var signature = Assert.Single(math.Groups[1].Value.Split('\n').Select(line => line.Trim()),
            line => Regex.IsMatch(line, @"^(?:asinh\b|""asinh""|'asinh'|\[\s*['""]asinh['""]\s*\])"));
        Assert.Equal("asinh(x: number): number;", signature);
        Write($"stage=declaration signature={signature}");
    }

    private void Write(FormattableString entry) =>
        output.WriteLine("C171 MathAsinh " + entry.ToString(CultureInfo.InvariantCulture));

    private BytecodeModule Compile(string label, string file, string source, TsType result, int arity)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, file).Tokenize());
        var syntax = parser.Parse();
        Write($"source={label} stage=parse count={parser.Diagnostics.Count}");
        foreach (var item in parser.Diagnostics)
            Write($"source={label} stage=parse-diagnostic severity={item.Severity} code={item.Code} numeric-code={(int)item.Code} file={Escape(item.Location.Source)} line={item.Location.Line} column={item.Location.Column} offset={item.Location.Offset} message={Escape(item.Message)}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        Write($"source={label} stage=bind count={diagnostics.Count}");
        foreach (var item in diagnostics)
            Write($"source={label} stage=bind-diagnostic severity={item.Severity} code={item.Code} numeric-code={(int)item.Code} file={Escape(item.Location.Source)} line={item.Location.Line} column={item.Location.Column} offset={item.Location.Offset} message={Escape(item.Message)}");
        Assert.Empty(diagnostics);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(result, main.Symbol.Type);
        Assert.Equal(arity, main.Symbol.Parameters.Count);
        Assert.All(main.Symbol.Parameters, parameter => Assert.Same(TsType.Number, parameter.Type));
        var block = Assert.IsType<BoundBlockStatement>(main.Body);
        var returned = Assert.IsType<BoundReturnStatement>(Assert.Single(block.Statements));
        var call = Assert.IsType<BoundCallExpression>(returned.Value);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.Equal("Math", Assert.IsType<ClassSymbol>(Assert.IsType<BoundVariableExpression>(access.Object).Symbol).Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("asinh", method.Name);
        Assert.Equal("Math", method.DeclaringClassName);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        Assert.Same(TsType.Number, call.Type);
        Assert.Equal(arity, call.Arguments.Count);
        Assert.All(call.Arguments, argument => Assert.Same(TsType.Number, argument.Type));

        var ir = new IRGenerator().Generate(bound);
        var instructions = Assert.Single(Assert.Single(ir.Functions, function => function.Name == "main").Blocks).Instructions;
        Assert.Equal(arity == 1 ? new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }
            : new[] { Opcode.Call, Opcode.Return }, instructions.Select(instruction => instruction.Opcode));
        if (arity == 1)
            Assert.Equal(0, instructions[0].Operand0);
        Assert.Equal(Target, Assert.IsType<string>(instructions[arity].OperandObject));
        Assert.Equal(arity, instructions[arity].Operand1);

        var module = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(module.Functions, function => function.Name == "main");
        var bytes = compiled.Instructions;
        int callOffset = arity == 1 ? 5 : 0;
        Assert.Equal(callOffset + 10, bytes.Length);
        if (callOffset != 0)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[0]);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4)));
        }
        Assert.Equal(Opcodes.Call, bytes[callOffset]);
        int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 1, 4));
        Assert.InRange(index, 0, compiled.StringConstants.Length - 1);
        string target = compiled.StringConstants[index];
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 5, 4));
        Assert.Equal(Target, target);
        Assert.Equal(arity, argc);
        Assert.Equal(Opcodes.Return, bytes[callOffset + 9]);
        Write($"source={label} stage=compiled target={target} argc={argc}");
        return module;
    }

    private static string Escape(string? value) =>
        value is null ? "\\null" : value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private void AssertPart(BytecodeModule module, string row, double expected, double? input = null)
    {
        TsValue[] args = input.HasValue
            ? new[] { (TsValue)Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(input.GetValueOrDefault())) }
            : Array.Empty<TsValue>();
        var value = Assert.IsType<TsFloat64Value>(new TypeSharp.VM.Interpreter.Interpreter().Execute(module, "main", args));
        Assert.Equal(TsValueType.Float64, value.ValueType);
        double actual = value.Value;
        string observed;
        if (row == "large-positive")
        {
            Assert.True(double.IsFinite(actual));
            Assert.True(actual > 690.0 && actual < 692.0);
            observed = actual.ToString("R", CultureInfo.InvariantCulture);
        }
        else if (row == "tiny-positive")
        {
            Assert.True(double.IsFinite(actual) && actual > 0.0);
            Assert.InRange(Math.Abs((actual - expected) / expected), 0.0, 1e-12);
            observed = actual.ToString("R", CultureInfo.InvariantCulture);
        }
        else if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual));
            observed = "NaN";
        }
        else if (double.IsInfinity(expected))
        {
            Assert.Equal(expected, actual);
            observed = double.IsNegativeInfinity(actual) ? "-Infinity" : "+Infinity";
        }
        else if (expected == 0.0)
        {
            var bits = BitConverter.DoubleToInt64Bits(actual);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), bits);
            observed = bits.ToString("x16", CultureInfo.InvariantCulture);
        }
        else
        {
            Assert.True(double.IsFinite(actual));
            Assert.InRange(Math.Abs(actual - expected), 0.0, 1e-12);
            observed = actual.ToString("R", CultureInfo.InvariantCulture);
        }
        Write($"row={row} type={value.ValueType} actual={observed}");
    }
}
