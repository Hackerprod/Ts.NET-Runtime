using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
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

public class MathTanTests
{
    private const string FactName = nameof(Tan_ReturnsTangentInRadians);
    private const string Target = "Math::tan";
    private readonly ITestOutputHelper _output;

    public MathTanTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Tan_ReturnsTangentInRadians()
    {
        var witness = Compile("witness", "c152-math-tan.ts",
            "function main(x: number): any { return Math.tan(x); }", TsType.Any);
        CheckRow(witness, "witness", "positive-zero", 0.0, 0.0, 0, "0000000000000000");
        var typed = Compile("typed", "c152-math-tan-typed.ts",
            "function main(x: number): number { return Math.tan(x); }", TsType.Number);
        double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        CheckRow(typed, "typed", "positive-zero", 0.0, 0.0, 0, "0000000000000000");
        CheckRow(typed, "typed", "negative-zero", negativeZero, negativeZero, 0, "8000000000000000");
        CheckRow(typed, "typed", "positive-pi-over-four", Math.PI / 4, 1, 1e-12);
        CheckRow(typed, "typed", "negative-pi-over-four", -Math.PI / 4, -1, 1e-12);
        CheckRow(typed, "typed", "positive-pi-over-six", Math.PI / 6, 0.5773502691896257, 1e-12);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        var math = Assert.Single(Regex.Matches(File.ReadAllText(path), @"(?ms)^interface Math[ \t]*\{\s*?(.*?)^\}"));
        var signatures = math.Groups[1].Value.Split('\n').Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, @"^tan\b")).ToArray();
        var signature = Assert.Single(signatures);
        Assert.Equal("tan(x: number): number;", signature);
        Log("DECL", new { fact = FactName, phase = "typed", signature, count = signatures.Length });
    }

    private BytecodeModule Compile(string phase, string sourceName, string source, TsType returnType)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, sourceName).Tokenize());
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var diagnostics = binder.Diagnostics.All.Select(item => new
        {
            severity = item.Severity.ToString(), code = item.Code.ToString(), message = item.Message,
            line = item.Location.Line, column = item.Location.Column, offset = item.Location.Offset
        }).ToArray();
        Log("BIND", new
        {
            fact = FactName, phase, sourceName, source, parserCount = parser.Diagnostics.Count(),
            binderCount = diagnostics.Length, diagnostics
        });
        if (phase == "witness")
            Assert.True(diagnostics.Length == 0, "C152_TAN_BINDER_FAILURE Tan_ReturnsTangentInRadians witness");
        else
            Assert.Empty(diagnostics);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        var parameter = Assert.Single(main.Symbol.Parameters);
        Assert.Equal("x", parameter.Name);
        Assert.False(parameter.IsRest);
        Assert.Same(TsType.Number, parameter.Type);
        Assert.Same(returnType, main.Symbol.Type);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, call.Type);
        Assert.Same(TsType.Number, Assert.Single(call.Arguments).Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<ClassSymbol>(Assert.IsType<BoundVariableExpression>(member.Object).Symbol);
        Assert.Equal("Math", receiver.Name);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("tan", method.Name);
        Assert.Equal(receiver.Name, method.DeclaringClassName);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);

        var ir = new IRGenerator().Generate(bound);
        var fn = Assert.Single(ir.Functions, function => function.Name == main.Symbol.Name);
        var irInstructions = Assert.Single(fn.Blocks).Instructions;
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }, irInstructions.Select(op => op.Opcode));
        Assert.Equal(0, irInstructions[0].Operand0);
        var irTarget = Assert.IsType<string>(irInstructions[1].OperandObject);
        Assert.Equal(Target, irTarget);
        Assert.Equal(1, irInstructions[1].Operand1);
        var compiled = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(compiled.Functions, function => function.Name == fn.Name);
        Assert.Equal(1, encoded.ParameterCount);
        Assert.Equal(-1, encoded.RestParameterIndex);
        var bytes = encoded.Instructions;
        Assert.Equal(15, bytes.Length);
        Assert.Equal(Opcodes.LoadArg, bytes[0]);
        Assert.Equal(Opcodes.Call, bytes[5]);
        Assert.Equal(Opcodes.Return, bytes[14]);
        var slot = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4));
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6, 4));
        var argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4));
        Assert.Equal(0, slot);
        Assert.InRange(index, 0, encoded.StringConstants.Length - 1);
        var target = encoded.StringConstants[index];
        Assert.Equal(irTarget, target);
        Assert.Equal(irInstructions[1].Operand1, argc);
        var irOps = new[] { $"LoadArg:{irInstructions[0].Operand0}", $"Call:{irTarget}/{irInstructions[1].Operand1}", "Return" };
        var bytecodeOps = new[] { $"LoadArg:{slot}", $"Call:{target}/{argc}", "Return" };
        Log("DISPATCH", new
        {
            fact = FactName, phase, entry = encoded.Name, parameter = parameter.Name, parameterType = "number",
            declaredReturn = ReferenceEquals(main.Symbol.Type, TsType.Any) ? "any" : "number", receiver = receiver.Name,
            methodReturn = "number", callReturn = "number", irOps, bytecodeOps, target, argc
        });
        return compiled;
    }

    private void CheckRow(BytecodeModule compiled, string phase, string row, double input,
        double expected, double tolerance, string? expectedBits = null)
    {
        var wrapped = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(input));
        Assert.Equal(TsValueType.Float64, wrapped.ValueType);
        var inputBits = Bits(wrapped.Value);
        Assert.Equal(Bits(input), inputBits);
        if (expectedBits is not null)
        {
            Assert.Equal(expectedBits, inputBits);
            Assert.Equal(expectedBits, Bits(expected));
        }

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsFloat64Value>(interpreter.Execute(compiled, "main", new TsValue[] { wrapped }));
        Assert.Equal(TsValueType.Float64, actual.ValueType);
        Assert.True(double.IsFinite(actual.Value));
        var actualBits = Bits(actual.Value);
        if (expectedBits is not null)
            Assert.Equal(expectedBits, actualBits);
        else
            Assert.InRange(Math.Abs(actual.Value - expected), 0, tolerance);
        Log("RUN", new
        {
            fact = FactName, phase, row, input = Format(wrapped.Value), inputBits,
            wrapper = actual.GetType().Name, tag = actual.ValueType.ToString(), expected = Format(expected),
            expectedBits, tolerance, actual = Format(actual.Value), actualBits
        });
    }

    private void Log(string kind, object value) => _output.WriteLine($"C152_TAN_{kind} {JsonSerializer.Serialize(value)}");

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

    private static string Format(double value) => value == 0
        ? (BitConverter.DoubleToInt64Bits(value) == long.MinValue ? "-0" : "0")
        : value.ToString("R", CultureInfo.InvariantCulture);
}
