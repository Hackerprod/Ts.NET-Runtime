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

public sealed class StringReplaceOmittedReplacementTests
{
    private readonly ITestOutputHelper _output;

    public StringReplaceOmittedReplacementTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Replace_OmittedReplacementInsertsUndefined()
    {
        const string source = """
            function hit(s: string): string { return s.replace("ba"); }
            function explicitVoid(s: string, replacement: any): string { return s.replace("ba", replacement); }
            function miss(s: string): string { return s.replace("zz"); }
            """;
        var tokens = new Lexer(source, "c184-string-replace-omitted.ts").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(binder.Diagnostics.All);

        Assert.Equal(3, bound.Members.Count);
        var functions = bound.Members.Select(member => Assert.IsType<BoundFunctionDeclaration>(member)).ToArray();
        CheckBinding(functions, "hit", "ba", 1);
        CheckBinding(functions, "explicitVoid", "ba", 2);
        CheckBinding(functions, "miss", "zz", 1);

        var ir = new IRGenerator().Generate(bound);
        var omittedCall = CheckRoute(Assert.Single(ir.Functions, function => function.Name == "hit"), "ba", 2);
        var explicitCall = CheckRoute(Assert.Single(ir.Functions, function => function.Name == "explicitVoid"), "ba", 3);
        var missCall = CheckRoute(Assert.Single(ir.Functions, function => function.Name == "miss"), "zz", 2);
        var module = BytecodeCompiler.Compile(ir);

        const string omittedHit = "omitted-hit";
        const string explicitUndefined = "explicit-undefined";
        const string omittedMiss = "omitted-miss";
        var mismatches = new List<string>();
        if (!Measure(module, "hit", omittedCall, omittedHit, "omitted", new[] { TsValue.FromString("ababa") }, "aundefinedba"))
            mismatches.Add(omittedHit);
        if (!Measure(module, "explicitVoid", explicitCall, explicitUndefined, "TsVoid", new[] { TsValue.FromString("ababa"), TsValue.Void }, "aundefinedba"))
            mismatches.Add(explicitUndefined);
        if (!Measure(module, "miss", missCall, omittedMiss, "omitted", new[] { TsValue.FromString("ababa") }, "ababa"))
            mismatches.Add(omittedMiss);

        if (mismatches.Count != 0)
            Assert.Fail($"C184_SEMANTIC_MISMATCHES {string.Join(",", mismatches)}");
    }

    private static void CheckBinding(BoundFunctionDeclaration[] functions, string name, string needle, int explicitCount)
    {
        var function = Assert.Single(functions, entry => entry.Symbol.Name == name);
        Assert.Equal(explicitCount, function.Symbol.Parameters.Count);
        Assert.Same(TsType.String, function.Symbol.Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.False(call.IsNullConditional);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(member.IsNullConditional);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        Assert.Same(function.Symbol.Parameters[0], Assert.IsType<ParameterSymbol>(receiver.Symbol));
        Assert.Equal("s", receiver.Symbol.Name);
        Assert.Same(TsType.String, receiver.Type);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal("replace", method.Name);
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.String, method.Type);
        Assert.Same(TsType.String, call.Type);
        Assert.Equal(explicitCount, call.Arguments.Count);
        var search = Assert.IsType<BoundLiteralExpression>(call.Arguments[0]);
        Assert.Same(TsType.String, Assert.IsType<TsLiteralType>(search.Type).BaseType);
        Assert.Equal(needle, Assert.IsType<string>(search.Value));
        if (explicitCount == 2)
        {
            var replacement = Assert.IsType<BoundVariableExpression>(call.Arguments[1]);
            Assert.Same(function.Symbol.Parameters[1], Assert.IsType<ParameterSymbol>(replacement.Symbol));
            Assert.Equal("replacement", replacement.Symbol.Name);
            Assert.Same(TsType.Any, replacement.Type);
        }
    }

    private static (string Target, int Argc) CheckRoute(FunctionIR function, string needle, int argc)
    {
        var entry = function.Blocks[0];
        var expected = argc == 2
            ? new[] { Opcode.LoadArg, Opcode.LoadConst_String, Opcode.Call, Opcode.Return }
            : new[] { Opcode.LoadArg, Opcode.LoadConst_String, Opcode.LoadArg, Opcode.Call, Opcode.Return };
        Assert.Equal(expected, entry.Instructions.Select(instruction => instruction.Opcode).ToArray());
        Assert.Equal(0, entry.Instructions[0].Operand0);
        Assert.Equal(needle, Assert.IsType<string>(entry.Instructions[1].OperandObject));
        if (argc == 3)
            Assert.Equal(1, entry.Instructions[2].Operand0);
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Same(entry.Instructions[argc], call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::replace", target);
        Assert.Equal(argc, call.Operand1);
        return (target, call.Operand1);
    }

    private bool Measure(BytecodeModule module, string function, (string Target, int Argc) route,
        string row, string input, TsValue[] operands, string expected)
    {
        Assert.Equal(route.Argc - 1, operands.Length); // The search literal is supplied by the compiled function.
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

        var result = (value as TsStringValue)?.Value;
        var ok = error is null && value is TsStringValue { ValueType: TsValueType.String }
            && string.Equals(result, expected, StringComparison.Ordinal);
        _output.WriteLine("C184 String.replace " + JsonSerializer.Serialize(new
        {
            row,
            target = route.Target,
            argc = route.Argc,
            input,
            wrapper = value?.GetType().Name,
            tag = value?.ValueType.ToString(),
            result,
            exception = error?.GetType().FullName,
            ok
        }));
        return ok;
    }
}
