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

public sealed class StringReplaceAllOmittedReplacementTests
{
    private readonly ITestOutputHelper _output;

    public StringReplaceAllOmittedReplacementTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReplaceAll_OmittedReplacementInsertsUndefined()
    {
        const string source = """
            function hit(s: string): string { return s.replaceAll("ba"); }
            function miss(s: string): string { return s.replaceAll("zz"); }
            function control(s: string, replacement: any): string { return s.replaceAll("ba", replacement); }
            """;
        var tokens = new Lexer(source, "c191-string-replaceall-omitted.ts").Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(binder.Diagnostics.All);
        var diagnostics = (Lexer: lexerErrors, Parser: parser.Diagnostics.Count(), Binder: binder.Diagnostics.All.Count());

        Assert.Equal(3, bound.Members.Count);
        var functions = bound.Members.Select(member => Assert.IsType<BoundFunctionDeclaration>(member)).ToArray();
        CheckBinding(functions, "hit", "ba", 1);
        CheckBinding(functions, "miss", "zz", 1);
        CheckBinding(functions, "control", "ba", 2);

        var ir = new IRGenerator().Generate(bound);
        var hitCall = CheckRoute(Assert.Single(ir.Functions, function => function.Name == "hit"), "ba", 2);
        var missCall = CheckRoute(Assert.Single(ir.Functions, function => function.Name == "miss"), "zz", 2);
        var controlCall = CheckRoute(Assert.Single(ir.Functions, function => function.Name == "control"), "ba", 3);
        var module = BytecodeCompiler.Compile(ir);

        const string omittedHit = "omitted-hit";
        const string omittedMiss = "omitted-miss";
        const string explicitUndefined = "explicit-undefined";
        const string explicitString = "explicit-string";
        var receiver = TsValue.FromString("ababa");
        var mismatches = new List<string>();
        if (!Measure(module, "hit", hitCall, diagnostics, omittedHit, new[] { receiver }, "aundefinedundefined"))
            mismatches.Add(omittedHit);
        if (!Measure(module, "miss", missCall, diagnostics, omittedMiss, new[] { receiver }, "ababa"))
            mismatches.Add(omittedMiss);
        if (!Measure(module, "control", controlCall, diagnostics, explicitUndefined,
                new[] { receiver, TsValue.Void }, "aundefinedundefined"))
            mismatches.Add(explicitUndefined);
        if (!Measure(module, "control", controlCall, diagnostics, explicitString,
                new[] { receiver, TsValue.FromString("!") }, "a!!"))
            mismatches.Add(explicitString);

        if (mismatches.Count != 0)
            Assert.Fail($"C191_SEMANTIC_MISMATCHES {string.Join(",", mismatches)}");
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
        Assert.Equal("replaceAll", method.Name);
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.String, method.Type);
        Assert.Same(TsType.String, call.Type);
        Assert.Equal(explicitCount, call.Arguments.Count);
        var search = Assert.IsType<BoundLiteralExpression>(call.Arguments[0]);
        Assert.Same(TsType.String, Assert.IsType<TsLiteralType>(search.Type).BaseType);
        var literal = Assert.IsType<string>(search.Value);
        Assert.NotEmpty(literal);
        Assert.Equal(needle, literal);
        if (explicitCount == 2)
        {
            var replacement = Assert.IsType<BoundVariableExpression>(call.Arguments[1]);
            Assert.Same(function.Symbol.Parameters[1], Assert.IsType<ParameterSymbol>(replacement.Symbol));
            Assert.Equal("replacement", replacement.Symbol.Name);
            Assert.Same(TsType.Any, replacement.Type);
        }
    }

    private static (string Target, int Argc, string Find) CheckRoute(FunctionIR function, string needle, int argc)
    {
        var entry = function.Blocks[0];
        var expected = argc == 2
            ? new[] { Opcode.LoadArg, Opcode.LoadConst_String, Opcode.Call, Opcode.Return }
            : new[] { Opcode.LoadArg, Opcode.LoadConst_String, Opcode.LoadArg, Opcode.Call, Opcode.Return };
        Assert.Equal(expected, entry.Instructions.Select(instruction => instruction.Opcode).ToArray());
        Assert.Equal(0, entry.Instructions[0].Operand0);
        var find = Assert.IsType<string>(entry.Instructions[1].OperandObject);
        Assert.Equal(needle, find);
        if (argc == 3)
            Assert.Equal(1, entry.Instructions[2].Operand0);
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Same(entry.Instructions[argc], call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::replaceAll", target);
        Assert.Equal(argc, call.Operand1);
        return (target, call.Operand1, find);
    }

    private bool Measure(BytecodeModule module, string function, (string Target, int Argc, string Find) route,
        (int Lexer, int Parser, int Binder) diagnostics, string row, TsValue[] operands, string expected)
    {
        Assert.Equal(route.Argc - 1, operands.Length); // The search literal is supplied by the compiled function.
        var input = Assert.IsType<TsStringValue>(operands[0]).Value;
        var replacement = operands.Length == 1 ? "omitted" :
            operands[1] is TsVoid ? "TsVoid" : Assert.IsType<TsStringValue>(operands[1]).Value;
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
        _output.WriteLine("C191 String.replaceAll " + JsonSerializer.Serialize(new
        {
            row,
            lexerErrors = diagnostics.Lexer,
            parserDiagnostics = diagnostics.Parser,
            binderDiagnostics = diagnostics.Binder,
            target = route.Target,
            argc = route.Argc,
            input,
            find = route.Find,
            replacement,
            expected,
            wrapper = value?.GetType().Name,
            tag = value?.ValueType.ToString(),
            result,
            exception = error?.GetType().FullName,
            ok
        }));
        return ok;
    }
}
