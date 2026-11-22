using System.Buffers.Binary;
using System.Text.RegularExpressions;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public class MathAtanTests
{
    private const string Target = "Math::atan";

    [Fact]
    public void Atan_ReturnsArctangentInRadians()
    {
        var witness = Compile("c157-math-atan.ts",
            "function main(x: number): any { return Math.atan(x); }", TsType.Any);
        CheckRow(witness, 0.0, 0.0);
        var typed = Compile("c157-math-atan-typed.ts",
            "function main(x: number): number { return Math.atan(x); }", TsType.Number);
        CheckRow(typed, 0.0, 0.0);
        CheckRow(typed, 1.0, 0.7853981633974483);
        CheckRow(typed, -1.0, -0.7853981633974483);
        CheckRow(typed, 0.5, 0.4636476090008061);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        var math = Assert.Single(Regex.Matches(File.ReadAllText(path), @"(?ms)^interface Math[ \t]*\{\s*?(.*?)^\}"));
        var signatures = math.Groups[1].Value.Split('\n').Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line, @"^atan\b")).ToArray();
        Assert.Equal("atan(x: number): number;", Assert.Single(signatures));
    }

    private static BytecodeModule Compile(string sourceName, string source, TsType returnType)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, sourceName).Tokenize());
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var diagnostics = binder.Diagnostics.All.ToArray();
        if (returnType == TsType.Any)
        {
            var details = diagnostics.Select(item => FormattableString.Invariant(
                $"{item.Severity}/{item.Code} {item.Location.Line}:{item.Location.Column} offset={item.Location.Offset}: {item.Message}"));
            var message = FormattableString.Invariant($"Math.atan witness binder diagnostics: count={diagnostics.Length}");
            Assert.True(diagnostics.Length == 0, message + "; " + string.Join("; ", details));
        }
        else
            Assert.Empty(diagnostics);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(TsType.Number, Assert.Single(main.Symbol.Parameters).Type);
        Assert.Same(returnType, main.Symbol.Type);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, call.Type);
        Assert.Same(TsType.Number, Assert.Single(call.Arguments).Type);

        var ir = new IRGenerator().Generate(bound);
        var fn = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = Assert.Single(fn.Blocks).Instructions;
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }, ops.Select(op => op.Opcode));
        Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(Target, Assert.IsType<string>(ops[1].OperandObject));
        Assert.Equal(1, ops[1].Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(compiled.Functions, function => function.Name == "main");
        var bytes = encoded.Instructions;
        Assert.Equal(15, bytes.Length);
        Assert.Equal(Opcodes.LoadArg, bytes[0]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4)));
        Assert.Equal(Opcodes.Call, bytes[5]);
        var index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6, 4));
        Assert.InRange(index, 0, encoded.StringConstants.Length - 1);
        Assert.Equal(Target, encoded.StringConstants[index]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(10, 4)));
        Assert.Equal(Opcodes.Return, bytes[14]);
        return compiled;
    }

    private static void CheckRow(BytecodeModule module, double input, double expected)
    {
        var wrapped = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(input));
        Assert.Equal(TsValueType.Float64, wrapped.ValueType);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsFloat64Value>(interpreter.Execute(module, "main", new TsValue[] { wrapped }));
        Assert.Equal(TsValueType.Float64, actual.ValueType);
        Assert.True(double.IsFinite(actual.Value));
        if (expected == 0.0)
            Assert.Equal(0.0, actual.Value);
        else
            Assert.InRange(Math.Abs(actual.Value - expected), 0.0, 1e-12);
    }
}
