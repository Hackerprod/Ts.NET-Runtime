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

public sealed class MathAcoshTests(ITestOutputHelper output)
{
    private const string Target = "Math::acosh";

    [Fact]
    public void Acosh_ReturnsPrincipalInverseHyperbolicCosine()
    {
        var witness = Build("w", "c169-math-acosh.ts",
            "function main(x: number): any { return Math.acosh(x); }", TsType.Any, 1);
        Observe(witness, "w-one", 1, 0);
        var typed = Build("t", "c169-math-acosh-typed.ts",
            "function main(x: number): number { return Math.acosh(x); }", TsType.Number, 1);
        foreach (var row in new (string Label, double Input, double Expected)[]
        {
            ("one", 1, 0), ("two", 2, 1.3169578969248166), ("ten", 10, 2.993222846126381),
            ("positive-infinity", double.PositiveInfinity, double.PositiveInfinity),
            ("below-domain", 0.5, double.NaN), ("negative", -1, double.NaN),
            ("negative-infinity", double.NegativeInfinity, double.NaN), ("nan", double.NaN, double.NaN)
        })
            Observe(typed, row.Label, row.Input, row.Expected);
        var omitted = Build("o", "c169-math-acosh-omitted.ts",
            "function main(): number { return Math.acosh(); }", TsType.Number, 0);
        Observe(omitted, "omitted", null, double.NaN);

        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string file = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        var math = Assert.Single(Regex.Matches(File.ReadAllText(file), @"(?ms)^interface Math[ \t]*\{(.*?)^\}"));
        string signature = Assert.Single(math.Groups[1].Value.Split('\n').Select(line => line.Trim()),
            line => Regex.IsMatch(line, @"^acosh\b"));
        Assert.Equal("acosh(x: number): number;", signature);
        Log($"stage=declaration signature={signature}");
    }

    private void Log(FormattableString detail) => output.WriteLine("C169 MathAcosh " + detail.ToString(CultureInfo.InvariantCulture));

    private BytecodeModule Build(string label, string name, string text, TsType expectedReturn, int expectedArgs)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(text, name).Tokenize());
        var syntax = parser.Parse();
        Log($"source={label} stage=parse count={parser.Diagnostics.Count}");
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        Log($"source={label} stage=bind count={diagnostics.Count}");
        foreach (var d in diagnostics)
            Log($"source={label} stage=bind-diagnostic severity={d.Severity} code={d.Code} numeric-code={(int)d.Code} file={Escape(d.Location.Source)} line={d.Location.Line} column={d.Location.Column} offset={d.Location.Offset} message={Escape(d.Message)}");
        Assert.Empty(diagnostics);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(expectedReturn, main.Symbol.Type);
        Assert.Equal(expectedArgs, main.Symbol.Parameters.Count());
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(Assert.IsType<BoundBlockStatement>(main.Body).Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        var math = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("acosh", method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        Assert.Same(TsType.Number, call.Type);
        Assert.Equal(expectedArgs, call.Arguments.Count());
        Assert.All(call.Arguments, arg => Assert.Same(TsType.Number, arg.Type));

        var ir = new IRGenerator().Generate(bound);
        var ops = Assert.Single(Assert.Single(ir.Functions, f => f.Name == "main").Blocks).Instructions;
        var expectedOps = expectedArgs == 1
            ? new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }
            : new[] { Opcode.Call, Opcode.Return };
        Assert.Equal(expectedOps, ops.Select(op => op.Opcode));
        if (expectedArgs == 1)
            Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(Target, Assert.IsType<string>(ops[expectedArgs].OperandObject));
        Assert.Equal(expectedArgs, ops[expectedArgs].Operand1);

        var module = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(module.Functions, f => f.Name == "main");
        var bytes = encoded.Instructions;
        int offset = expectedArgs * 5;
        Assert.Equal(offset + 10, bytes.Length);
        if (expectedArgs == 1)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[0]);
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4)));
        }
        Assert.Equal(Opcodes.Call, bytes[offset]);
        int constant = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
        Assert.InRange(constant, 0, encoded.StringConstants.Length - 1);
        string target = encoded.StringConstants[constant];
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4));
        Assert.Equal(Target, target);
        Assert.Equal(expectedArgs, argc);
        Assert.Equal(Opcodes.Return, bytes[offset + 9]);
        Log($"source={label} stage=compiled target={target} argc={argc}");
        return module;
    }

    private static string Escape(string? value) => (value ?? "\\null")
        .Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private void Observe(BytecodeModule module, string label, double? input, double expected)
    {
        var args = input.HasValue ? new TsValue[] { TsValue.FromFloat64(input.Value) } : Array.Empty<TsValue>();
        var actual = Assert.IsType<TsFloat64Value>(new TypeSharp.VM.Interpreter.Interpreter().Execute(module, "main", args));
        Assert.Equal(TsValueType.Float64, actual.ValueType);
        double number = actual.Value;
        string observed;
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(number));
            observed = "NaN";
        }
        else if (double.IsPositiveInfinity(expected))
        {
            Assert.True(double.IsPositiveInfinity(number));
            observed = "+Infinity";
        }
        else if (expected == 0)
        {
            long bits = BitConverter.DoubleToInt64Bits(number);
            Assert.Equal(0L, bits);
            observed = bits.ToString("x16", CultureInfo.InvariantCulture);
        }
        else
        {
            Assert.True(double.IsFinite(number));
            Assert.InRange(Math.Abs(number - expected), 0, 1e-12);
            observed = number.ToString("R", CultureInfo.InvariantCulture);
        }
        Log($"row={label} type={actual.ValueType} actual={observed}");
    }
}
