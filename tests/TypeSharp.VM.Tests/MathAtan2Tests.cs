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

public class MathAtan2Tests(ITestOutputHelper output)
{
    private const string Target = "Math::atan2";
    private const double Pi = 3.141592653589793;
    private const double HalfPi = 1.5707963267948966;
    private const double QuarterPi = 0.7853981633974483;
    private const double ThreeQuarterPi = 2.356194490192345;
    private const double Asymmetric = 0.4636476090008061;

    [Fact]
    public void Atan2_ReturnsQuadrantAwareAngleInRadians()
    {
        var witness = Compile("w", "c172-math-atan2.ts",
            "function main(y: number, x: number): any { return Math.atan2(y, x); }", TsType.Any, "y", "x");
        Run(witness, "w-quadrant-two", ThreeQuarterPi, 1, -1);

        var typed = Compile("t", "c172-math-atan2-typed.ts",
            "function main(y: number, x: number): number { return Math.atan2(y, x); }", TsType.Number, "y", "x");
        double pz = 0.0, nz = BitConverter.Int64BitsToDouble(long.MinValue);
        double pinf = double.PositiveInfinity, ninf = double.NegativeInfinity, nan = double.NaN;
        Run(typed, "q1-asymmetric", Asymmetric, 1, 2);
        Run(typed, "q2", ThreeQuarterPi, 1, -1);
        Run(typed, "q3", -ThreeQuarterPi, -1, -1);
        Run(typed, "q4", -QuarterPi, -1, 1);
        Run(typed, "pz-pz", pz, pz, pz);
        Run(typed, "nz-pz", nz, nz, pz);
        Run(typed, "pz-nz", Pi, pz, nz);
        Run(typed, "nz-nz", -Pi, nz, nz);
        Run(typed, "pz-neg", Pi, pz, -1);
        Run(typed, "nz-neg", -Pi, nz, -1);
        Run(typed, "pos-pz", HalfPi, 1, pz);
        Run(typed, "neg-pz", -HalfPi, -1, pz);
        Run(typed, "pos-nz", HalfPi, 1, nz);
        Run(typed, "neg-nz", -HalfPi, -1, nz);
        Run(typed, "pos-pinf", pz, 1, pinf);
        Run(typed, "neg-pinf", nz, -1, pinf);
        Run(typed, "pos-ninf", Pi, 1, ninf);
        Run(typed, "neg-ninf", -Pi, -1, ninf);
        Run(typed, "pinf-pos", HalfPi, pinf, 1);
        Run(typed, "ninf-pos", -HalfPi, ninf, 1);
        Run(typed, "pinf-pinf", QuarterPi, pinf, pinf);
        Run(typed, "pinf-ninf", ThreeQuarterPi, pinf, ninf);
        Run(typed, "ninf-pinf", -QuarterPi, ninf, pinf);
        Run(typed, "ninf-ninf", -ThreeQuarterPi, ninf, ninf);
        Run(typed, "nan-y", nan, nan, 1);
        Run(typed, "nan-x", nan, 1, nan);

        var zero = Compile("z", "c172-math-atan2-zero.ts",
            "function main(): number { return Math.atan2(); }", TsType.Number);
        Run(zero, "omitted-both", nan);
        var one = Compile("o", "c172-math-atan2-one.ts",
            "function main(y: number): number { return Math.atan2(y); }", TsType.Number, "y");
        Run(one, "omitted-x", nan, 1);
        var extra = Compile("e", "c172-math-atan2-extra.ts",
            "function main(y: number, x: number, unused: number): number { return Math.atan2(y, x, unused); }",
            TsType.Number, "y", "x", "unused");
        Run(extra, "ignored-third", Asymmetric, 1, 2, nan);

        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var declarations = File.ReadAllText(Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var math = Assert.Single(Regex.Matches(declarations, @"(?ms)^interface Math[ \t]*\{(.*?)^\}"));
        var signature = Assert.Single(Regex.Matches(math.Groups[1].Value,
            @"(?m)(?:^|(?<=;))\s*((?:atan2\b|""atan2""|'atan2'|\[\s*['""]atan2['""]\s*\])[^;\r\n]*;)"));
        Assert.Equal("atan2(y: number, x: number): number;", signature.Groups[1].Value);
        Write($"stage=declaration signature={signature.Groups[1].Value}");
    }

    private void Write(FormattableString row) =>
        output.WriteLine("C172 MathAtan2 " + row.ToString(CultureInfo.InvariantCulture));

    private static string Escape(string? s) =>
        s is null ? "\\null" : s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private BytecodeModule Compile(string label, string file, string source, TsType result, params string[] parameters)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, file).Tokenize());
        var syntax = parser.Parse();
        Write($"source={label} stage=parse count={parser.Diagnostics.Count}");
        foreach (var d in parser.Diagnostics)
            Write($"source={label} stage=parse-diagnostic severity={d.Severity} code={d.Code} numeric-code={(int)d.Code} file={Escape(d.Location.Source)} line={d.Location.Line} column={d.Location.Column} offset={d.Location.Offset} message={Escape(d.Message)}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        Write($"source={label} stage=bind count={diagnostics.Count}");
        foreach (var d in diagnostics)
            Write($"source={label} stage=bind-diagnostic severity={d.Severity} code={d.Code} numeric-code={(int)d.Code} file={Escape(d.Location.Source)} line={d.Location.Line} column={d.Location.Column} offset={d.Location.Offset} message={Escape(d.Message)}");
        Assert.Empty(diagnostics);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(result, main.Symbol.Type);
        Assert.Equal(parameters, main.Symbol.Parameters.Select(p => p.Name));
        var block = Assert.IsType<BoundBlockStatement>(main.Body);
        var returned = Assert.IsType<BoundReturnStatement>(Assert.Single(block.Statements));
        var call = Assert.IsType<BoundCallExpression>(returned.Value);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.Equal("Math", Assert.IsType<ClassSymbol>(Assert.IsType<BoundVariableExpression>(access.Object).Symbol).Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("atan2", method.Name);
        Assert.Equal("Math", method.DeclaringClassName);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        Assert.Same(TsType.Number, call.Type);
        int arity = parameters.Length;
        Assert.Equal(arity, call.Arguments.Count);
        for (int i = 0; i < arity; i++)
        {
            var boundArg = Assert.IsType<BoundVariableExpression>(call.Arguments[i]);
            Assert.Same(main.Symbol.Parameters[i], boundArg.Symbol);
            Assert.Same(TsType.Number, boundArg.Type);
        }

        var ir = new IRGenerator().Generate(bound);
        var instructions = Assert.Single(Assert.Single(ir.Functions, f => f.Name == "main").Blocks).Instructions;
        Assert.Equal(Enumerable.Repeat(Opcode.LoadArg, arity).Concat(new[] { Opcode.Call, Opcode.Return }),
            instructions.Select(i => i.Opcode));
        for (int i = 0; i < arity; i++)
            Assert.Equal(i, instructions[i].Operand0);
        Assert.Equal(Target, Assert.IsType<string>(instructions[arity].OperandObject));
        Assert.Equal(arity, instructions[arity].Operand1);

        var module = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(module.Functions, f => f.Name == "main");
        var bytes = compiled.Instructions;
        int at = arity * 5;
        Assert.Equal(at + 10, bytes.Length);
        for (int i = 0; i < arity; i++)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[i * 5]);
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 5 + 1, 4)));
        }
        Assert.Equal(Opcodes.Call, bytes[at]);
        int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 1, 4));
        Assert.InRange(index, 0, compiled.StringConstants.Length - 1);
        string target = compiled.StringConstants[index];
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 5, 4));
        Assert.Equal(Target, target);
        Assert.Equal(arity, argc);
        Assert.Equal(Opcodes.Return, bytes[at + 9]);
        Write($"source={label} stage=compiled target={target} argc={argc}");
        return module;
    }

    private void Run(BytecodeModule module, string row, double expected, params double[] inputs)
    {
        var args = inputs.Select(input => (TsValue)Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(input))).ToArray();
        var result = Assert.IsType<TsFloat64Value>(new TypeSharp.VM.Interpreter.Interpreter().Execute(module, "main", args));
        Assert.Equal(TsValueType.Float64, result.ValueType);
        double observed = result.Value;
        string value;
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(observed));
            value = "NaN";
        }
        else if (expected == 0.0)
        {
            long bits = BitConverter.DoubleToInt64Bits(observed);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), bits);
            value = bits.ToString("x16", CultureInfo.InvariantCulture);
        }
        else
        {
            Assert.True(double.IsFinite(observed) && observed != 0.0 && Math.Sign(expected) == Math.Sign(observed));
            Assert.InRange(Math.Abs(observed - expected), 0.0, 1e-12);
            value = observed.ToString("R", CultureInfo.InvariantCulture);
        }
        Write($"row={row} type={result.ValueType} actual={value}");
    }
}
