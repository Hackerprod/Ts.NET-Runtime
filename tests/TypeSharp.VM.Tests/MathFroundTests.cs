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

public sealed class MathFroundTests(ITestOutputHelper output)
{
    private const string Target = "Math::fround";

    [Fact]
    public void Fround_RoundsToBinary32AndWidensToFloat64()
    {
        var witness = Compile("w", "c173-math-fround.ts",
            "function main(x: number): any { return Math.fround(x); }", TsType.Any, "x");
        Check(witness, "witness", "4170000000000000", 16777217);

        var typed = Compile("t", "c173-math-fround-typed.ts",
            "function main(x: number): number { return Math.fround(x); }", TsType.Number, "x");
        double halfSubnormal = Math.ScaleB(1.0, -150);
        double maxFinite = Math.ScaleB(2.0 - Math.ScaleB(1.0, -23), 127);
        double overflow = Math.ScaleB(1.0, 128);
        Check(typed, "exact", "3ff8000000000000", 1.5);
        Check(typed, "fraction", "3fb99999a0000000", 0.1);
        Check(typed, "tie-down", "4170000000000000", 16777217);
        Check(typed, "tie-up", "4170000040000000", 16777219);
        Check(typed, "tie-negative", "c170000000000000", -16777217);
        Check(typed, "pos-zero", "0000000000000000", 0.0);
        Check(typed, "neg-zero", "8000000000000000", BitConverter.Int64BitsToDouble(long.MinValue));
        Check(typed, "pos-underflow-tie", "0000000000000000", halfSubnormal);
        Check(typed, "neg-underflow-tie", "8000000000000000", -halfSubnormal);
        Check(typed, "min-subnormal", "36a0000000000000", Math.ScaleB(1.0, -149));
        Check(typed, "subnormal-tie-up", "36b0000000000000", 3.0 * halfSubnormal);
        Check(typed, "pos-max-finite", "47efffffe0000000", maxFinite);
        Check(typed, "neg-max-finite", "c7efffffe0000000", -maxFinite);
        Check(typed, "pos-overflow", "7ff0000000000000", overflow);
        Check(typed, "neg-overflow", "fff0000000000000", -overflow);
        Check(typed, "pos-inf", "7ff0000000000000", double.PositiveInfinity);
        Check(typed, "neg-inf", "fff0000000000000", double.NegativeInfinity);
        Check(typed, "nan", "NaN", double.NaN);

        var omitted = Compile("z", "c173-math-fround-zero.ts",
            "function main(): number { return Math.fround(); }", TsType.Number);
        Check(omitted, "omitted", "NaN");

        var extra = Compile("e", "c173-math-fround-extra.ts",
            "function main(x: number, ignored: number): number { return Math.fround(x, ignored); }",
            TsType.Number, "x", "ignored");
        Check(extra, "ignored-extra", "4170000040000000", 16777219, double.NaN);

        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        var declaration = File.ReadAllText(path);
        var math = Assert.Single(Regex.Matches(declaration, @"(?m)^interface[ \t]+Math\b[^\S\n]*\{([^{}]*)\}"));
        var members = math.Groups[1].Value.Split(';');
        int member = Assert.Single(Enumerable.Range(0, members.Length),
            index => Regex.IsMatch(members[index].TrimStart(), @"^fround\b"));
        Assert.True(member < members.Length - 1, "Math.fround must end in a semicolon");
        string signature = members[member].Trim() + ";";
        Assert.Equal("fround(x: number): number;", signature);
        Log($"stage=declaration signature={signature}");
    }

    private BytecodeModule Compile(string label, string file, string source, TsType returnType,
        params string[] parameters)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, file).Tokenize());
        var parsed = parser.Parse();
        Log($"source={label} stage=parse count={parser.Diagnostics.Count}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(parsed);
        var diagnostics = binder.Diagnostics.All.ToArray();
        Log($"source={label} stage=bind count={diagnostics.Length}");
        foreach (var diagnostic in diagnostics)
        {
            var place = diagnostic.Location;
            Log($"source={label} stage=bind-diagnostic severity={diagnostic.Severity} code={diagnostic.Code} numeric-code={(int)diagnostic.Code} file={place.Source} line={place.Line} column={place.Column} offset={place.Offset} message={diagnostic.Message}");
        }
        Assert.Empty(binder.Diagnostics.All);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(returnType, function.Symbol.Type);
        Assert.Equal(parameters, function.Symbol.Parameters.Select(parameter => parameter.Name).ToArray());
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var returned = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(returned.Value);
        Assert.Same(TsType.Number, call.Type);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        Assert.Equal("Math", Assert.IsType<ClassSymbol>(receiver.Symbol).Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("Math", method.DeclaringClassName);
        Assert.Equal("fround", method.Name);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        int argc = parameters.Length;
        Assert.Equal(argc, call.Arguments.Count);
        if (argc == 2)
            Assert.NotSame(function.Symbol.Parameters[0], function.Symbol.Parameters[1]);
        for (int i = 0; i < argc; i++)
        {
            var argument = Assert.IsType<BoundVariableExpression>(call.Arguments[i]);
            Assert.Same(TsType.Number, argument.Type);
            Assert.Same(function.Symbol.Parameters[i], argument.Symbol);
        }

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, candidate => candidate.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.Equal(argc + 2, instructions.Length);
        for (int i = 0; i < argc; i++)
        {
            Assert.Equal(Opcode.LoadArg, instructions[i].Opcode);
            Assert.Equal(i, instructions[i].Operand0);
        }
        Assert.Equal(Opcode.Call, instructions[argc].Opcode);
        Assert.Equal(Target, Assert.IsType<string>(instructions[argc].OperandObject));
        Assert.Equal(argc, instructions[argc].Operand1);
        Assert.Equal(Opcode.Return, instructions[argc + 1].Opcode);

        var module = BytecodeCompiler.Compile(ir);
        var emitted = Assert.Single(module.Functions, candidate => candidate.Name == "main");
        Assert.Equal(argc, emitted.ParameterCount);
        using var reader = new BinaryReader(new MemoryStream(emitted.Instructions, writable: false));
        for (int i = 0; i < argc; i++)
        {
            Assert.Equal(Opcodes.LoadArg, reader.ReadByte());
            Assert.Equal(i, reader.ReadInt32());
        }
        Assert.Equal(Opcodes.Call, reader.ReadByte());
        var target = emitted.StringConstants[reader.ReadInt32()];
        Assert.Equal(Target, target);
        int actualCount = reader.ReadInt32();
        Assert.Equal(argc, actualCount);
        Assert.Equal(Opcodes.Return, reader.ReadByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        Log($"source={label} stage=compiled target={target} argc={actualCount}");
        return module;
    }

    private void Check(BytecodeModule module, string row, string expected, params double[] arguments)
    {
        var actual = Assert.IsType<TsFloat64Value>(new TypeSharp.VM.Interpreter.Interpreter().Execute(
            module, "main", arguments.Select(TsValue.FromFloat64).ToArray()));
        Assert.Equal(TsValueType.Float64, actual.ValueType);
        string bits = double.IsNaN(actual.Value) ? "NaN" : BitConverter.DoubleToInt64Bits(actual.Value).ToString("x16");
        Log($"row={row} type={actual.ValueType} actual={bits}");
        Assert.Equal(expected, bits);
    }

    private void Log(FormattableString message) => output.WriteLine("C173 MathFround " + message.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
