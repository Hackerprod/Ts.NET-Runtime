using System.Text.RegularExpressions;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class MathSinTests
{
    private readonly ITestOutputHelper _output;

    public MathSinTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Sin_ReturnsSineInRadians()
    {
        Bind("function main(x: number): any { return Math.sin(x); }", "c136-math-sin.ts", "sin");
        var compiled = Compile(Bind("function main(x: number): number { return Math.sin(x); }",
            "c136-math-sin-typed.ts", "sin"), "sin");
        double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        CheckRow(compiled, "sin", "positive-zero", 0.0, 0.0, 0, "0000000000000000");
        CheckRow(compiled, "sin", "negative-zero", negativeZero, negativeZero, 0, "8000000000000000");
        CheckRow(compiled, "sin", "positive-pi-over-six", Math.PI / 6, 0.5, 1e-12);
        CheckRow(compiled, "sin", "negative-pi-over-six", -Math.PI / 6, -0.5, 1e-12);
        CheckRow(compiled, "sin", "pi-over-two", Math.PI / 2, 1.0, 1e-12);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        var declarations = File.ReadAllText(path);
        var math = Assert.Single(Regex.Matches(declarations, @"(?ms)^interface Math[ \t]*\{\s*?(.*?)^\}"));
        var member = Assert.Single(math.Groups[1].Value.Split('\n')
            .Select(line => line.Trim()).Where(line => Regex.IsMatch(line, @"^sin\b")));
        Assert.Equal("sin(x: number): number;", member);
        _output.WriteLine($"declaration path={path}; Math own signature={member}; count=1");
    }

    [Fact]
    public void Sign_UsesExistingStaticDispatch()
    {
        var compiled = Compile(Bind("function main(x: number): number { return Math.sign(x); }",
            "c136-math-sign-control.ts", "sign"), "sign");
        CheckRow(compiled, "sign", "negative-pi-over-six", -Math.PI / 6, -1, 0);
        CheckRow(compiled, "sign", "positive-pi-over-six", Math.PI / 6, 1, 0);
    }

    private BoundSourceFile Bind(string source, string sourceName, string memberName)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source, sourceName).Tokenize());
        var tree = parser.Parse();
        LogDiagnostics("parser", sourceName, parser.Diagnostics);
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(tree);
        LogDiagnostics("binder", sourceName, binder.Diagnostics.All);
        Assert.Empty(binder.Diagnostics.All);

        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, call.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<ClassSymbol>(Assert.IsType<BoundVariableExpression>(member.Object).Symbol);
        Assert.Equal("Math", receiver.Name);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal(memberName, method.Name);
        Assert.Equal("Math", method.DeclaringClassName);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        Assert.Same(TsType.Number, Assert.Single(call.Arguments).Type);
        _output.WriteLine($"bound input={sourceName}; receiver=ClassSymbol:{receiver.Name}; method=MethodSymbol:{method.DeclaringClassName}::{method.Name}; parameters={method.Parameters.Count}; argument=Number; return=Number");
        return bound;
    }

    private void LogDiagnostics(string phase, string sourceName, IEnumerable<Diagnostic> diagnostics)
    {
        var rows = diagnostics.ToArray();
        _output.WriteLine($"diagnostics phase={phase}; input={sourceName}; count={rows.Length}");
        foreach (var diagnostic in rows)
        {
            var location = diagnostic.Location;
            _output.WriteLine($"diagnostic phase={phase}; severity={diagnostic.Severity}; code={diagnostic.Code}({(int)diagnostic.Code}); moduleId={diagnostic.ModuleId ?? "<null>"}; source={location.Source}; line={location.Line}; column={location.Column}; offset={location.Offset}; message={diagnostic.Message}");
        }
    }

    private BytecodeModule Compile(BoundSourceFile bound, string member)
    {
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        foreach (var block in main.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];
                _output.WriteLine($"ir Math.{member}; block={block.Id}; index={i}; opcode={instruction.Opcode}; operand0={instruction.Operand0}; operand1={instruction.Operand1}; operandObject={instruction.OperandObject ?? "<null>"}");
            }
        }

        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal($"Math::{member}", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(1, call.Operand1);
        var callBlock = Assert.Single(main.Blocks, block => block.Instructions.Contains(call));
        int index = callBlock.Instructions.IndexOf(call);
        Assert.True(index > 0);
        Assert.Equal(Opcode.LoadArg, callBlock.Instructions[index - 1].Opcode);
        Assert.Equal(0, callBlock.Instructions[index - 1].Operand0);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is
            Opcode.LoadConst_Void or Opcode.CallVirt or Opcode.CallDynamic);
        _output.WriteLine($"dispatch target={call.OperandObject}; argc={call.Operand1}; preceding=LoadArg 0");
        return BytecodeCompiler.Compile(ir);
    }

    private void CheckRow(BytecodeModule compiled, string member, string row, double input,
        double expected, double tolerance, string? expectedBits = null)
    {
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsFloat64Value>(
            interpreter.Execute(compiled, "main", new[] { TsValue.FromFloat64(input) }));
        var resultBits = Bits(actual.Value);
        var ok = double.IsFinite(actual.Value) && Math.Abs(actual.Value - expected) <= tolerance
            && (expectedBits is null || resultBits == expectedBits);
        _output.WriteLine(FormattableString.Invariant(
            $"result Math.{member}; row={row}; input={input:R}; inputBits={Bits(input)}; result={actual.Value:R}; resultBits={resultBits}; wrapper={actual.GetType().Name}; tag={actual.ValueType}; expected={expected:R}; expectedBits={expectedBits ?? "<numeric>"}; tolerance={tolerance:R}; outcome={(ok ? "pass" : "fail")}"));
        Assert.Equal(TsValueType.Float64, actual.ValueType);
        Assert.True(ok, $"Math.{member} did not match the analytical or exact-bit expectation for {row}");
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16");
}
