using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathSinOmittedArgumentTests
{
    private readonly ITestOutputHelper _output;

    public MathSinOmittedArgumentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Sin_OmittedAngleReturnsNaN()
    {
        const string source = "function omitted(): number { return Math.sin(); } function supplied(x: any): number { return Math.sin(x); }";
        var tokens = new Lexer(source, "c183-math-sin-omitted.ts").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(binder.Diagnostics.All);

        Assert.Equal(2, bound.Members.Count);
        var functions = bound.Members.Select(member => Assert.IsType<BoundFunctionDeclaration>(member)).ToArray();
        CheckBinding(functions, "omitted", 0);
        CheckBinding(functions, "supplied", 1);

        var ir = new IRGenerator().Generate(bound);
        var omittedCall = CheckRoute(Assert.Single(ir.Functions, f => f.Name == "omitted"), 0);
        var suppliedCall = CheckRoute(Assert.Single(ir.Functions, f => f.Name == "supplied"), 1);
        var module = BytecodeCompiler.Compile(ir);

        const string omitted = "omitted";
        const string explicitUndefined = "explicit-undefined";
        const string negativeZero = "negative-zero";
        const string negativeZeroBits = "8000000000000000";
        var mismatches = new List<string>();
        if (!Measure(module, "omitted", omittedCall, omitted, "none", Array.Empty<TsValue>(), null))
            mismatches.Add(omitted);
        if (!Measure(module, "supplied", suppliedCall, explicitUndefined, "TsVoid", new[] { TsValue.Void }, null))
            mismatches.Add(explicitUndefined);
        var signedZero = TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue));
        if (!Measure(module, "supplied", suppliedCall, negativeZero, negativeZeroBits, new[] { signedZero }, negativeZeroBits))
            mismatches.Add(negativeZero);

        if (mismatches.Count != 0)
            Assert.Fail($"C183_SEMANTIC_MISMATCHES {string.Join(",", mismatches)}");
    }

    private static void CheckBinding(BoundFunctionDeclaration[] functions, string name, int argc)
    {
        var function = Assert.Single(functions, f => f.Symbol.Name == name);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        Assert.Equal("Math", Assert.IsType<ClassSymbol>(receiver.Symbol).Name);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("Math", method.DeclaringClassName);
        Assert.Equal("sin", method.Name);
        Assert.Empty(method.Parameters);
        Assert.Equal(argc, call.Arguments.Count);
        Assert.Same(TsType.Number, method.Type);
        Assert.Same(TsType.Number, call.Type);
    }

    private static (string Target, int Argc) CheckRoute(FunctionIR function, int argc)
    {
        var entry = function.Blocks[0];
        var expected = argc == 0
            ? new[] { Opcode.Call, Opcode.Return }
            : new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return };
        Assert.Equal(expected, entry.Instructions.Select(instruction => instruction.Opcode).ToArray());
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Same(entry.Instructions[argc], call);
        if (argc == 1)
            Assert.Equal(0, entry.Instructions[0].Operand0);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Math::sin", target);
        Assert.Equal(argc, call.Operand1);
        return (target, call.Operand1);
    }

    private bool Measure(BytecodeModule module, string function, (string Target, int Argc) route,
        string row, string input, TsValue[] operands, string? expectedBits)
    {
        Assert.Equal(route.Argc, operands.Length);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? value = null;
        Exception? error = null;
        try
        {
            value = interpreter.Execute(module, function, operands);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var number = value as TsFloat64Value;
        var bits = number is null || double.IsNaN(number.Value) ? null
            : BitConverter.DoubleToInt64Bits(number.Value).ToString("X16", CultureInfo.InvariantCulture);
        var result = number is null ? null : bits == "8000000000000000" ? "-0"
            : number.Value.ToString("R", CultureInfo.InvariantCulture);
        var ok = error is null && number is { ValueType: TsValueType.Float64 }
            && (expectedBits is null ? double.IsNaN(number.Value) : bits == expectedBits);
        _output.WriteLine("C183 Math.sin " + JsonSerializer.Serialize(new
        {
            row,
            target = route.Target,
            argc = route.Argc,
            input,
            wrapper = value?.GetType().Name,
            tag = value?.ValueType.ToString(),
            result,
            bits,
            exception = error?.GetType().FullName,
            ok
        }));
        return ok;
    }
}
