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

public sealed class StringReplaceAllEmptyUndefinedReplacementTests(ITestOutputHelper output)
{
    [Fact]
    public void ReplaceAll_EmptySearchWithUndefinedReplacementInsertsAtBoundaries()
    {
        const string source = """
            function omitted(s: string): string { return s.replaceAll(""); }
            function explicitUndefined(s: string): string { return s.replaceAll("", undefined); }
            function explicitString(s: string): string { return s.replaceAll("", "-"); }
            """;
        var tokens = new Lexer(source, "c193-string-replaceall-empty-undefined.ts").Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        Assert.Equal(TokenKind.Identifier, Assert.Single(tokens, token => token.Text == "undefined").Kind);
        var literals = tokens.Where(token => token.Kind == TokenKind.StringLiteral)
            .Select(token => Assert.IsType<string>(token.Value)).ToArray();
        Assert.Equal(new[] { "", "", "", "-" }, literals);

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);
        var diagnostics = (Lexer: lexerErrors, Parser: parser.Diagnostics.Count(), Binder: binder.Diagnostics.All.Count());

        (string Row, string Function, Opcode? ReplacementOpcode, string Expected)[] rows =
        [
            ("omitted-empty", "omitted", null, "undefinedaundefinedbundefined"),
            ("explicit-undefined-empty", "explicitUndefined", Opcode.LoadConst_Void, "undefinedaundefinedbundefined"),
            ("explicit-string-control", "explicitString", Opcode.LoadConst_String, "-a-b-"),
        ];
        Assert.Equal(rows.Length, bound.Members.Count);
        var functions = bound.Members.Select(member => Assert.IsType<BoundFunctionDeclaration>(member)).ToArray();
        foreach (var row in rows)
            CheckBinding(Assert.Single(functions, function => function.Symbol.Name == row.Function), row.ReplacementOpcode);

        var ir = new IRGenerator().Generate(bound);
        var routes = rows.Select(row => CheckRoute(
            Assert.Single(ir.Functions, function => function.Name == row.Function), row.ReplacementOpcode)).ToArray();
        var module = BytecodeCompiler.Compile(ir);

        const string input = "ab";
        var mismatches = new List<string>();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var route = routes[i];
            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue[] operands = [TsValue.FromString(input)];
            TsValue? value = null;
            Exception? error = null;
            try
            {
                value = interpreter.Execute(module, row.Function, operands);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            var result = (value as TsStringValue)?.Value;
            var ok = error is null && value is TsStringValue { ValueType: TsValueType.String }
                && string.Equals(result, row.Expected, StringComparison.Ordinal);
            output.WriteLine("C193 String.replaceAll " + JsonSerializer.Serialize(new
            {
                row = row.Row,
                lexerErrors = diagnostics.Lexer,
                parserDiagnostics = diagnostics.Parser,
                binderDiagnostics = diagnostics.Binder,
                target = route.Target,
                argc = route.Argc,
                input,
                find = route.Find,
                replacement = route.Replacement,
                replacementValue = route.ReplacementValue,
                expected = row.Expected,
                wrapper = value?.GetType().Name,
                tag = value?.ValueType.ToString(),
                result,
                exception = error?.GetType().FullName,
                ok
            }));
            if (!ok)
                mismatches.Add(row.Row);
        }

        if (mismatches.Count != 0)
            Assert.Fail($"C193_SEMANTIC_MISMATCHES {string.Join(",", mismatches)}");
    }

    private static void CheckBinding(BoundFunctionDeclaration function, Opcode? replacementOpcode)
    {
        var parameter = Assert.Single(function.Symbol.Parameters);
        Assert.Equal("s", parameter.Name);
        Assert.Same(TsType.String, parameter.Type);
        Assert.Same(TsType.String, function.Symbol.Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.False(call.IsNullConditional);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(member.IsNullConditional);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(receiver.Symbol));
        Assert.Same(TsType.String, receiver.Type);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal("replaceAll", method.Name);
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.String, method.Type);
        Assert.Same(TsType.String, call.Type);
        Assert.Equal(replacementOpcode is null ? 1 : 2, call.Arguments.Count);
        CheckStringLiteral(call.Arguments[0], "");
        if (replacementOpcode == Opcode.LoadConst_Void)
        {
            var replacement = Assert.IsType<BoundLiteralExpression>(call.Arguments[1]);
            Assert.Null(replacement.Value);
            Assert.Same(TsType.Undefined, replacement.Type);
        }
        else if (replacementOpcode == Opcode.LoadConst_String)
        {
            CheckStringLiteral(call.Arguments[1], "-");
        }
    }

    private static void CheckStringLiteral(BoundNode node, string expected)
    {
        var literal = Assert.IsType<BoundLiteralExpression>(node);
        Assert.Same(TsType.String, Assert.IsType<TsLiteralType>(literal.Type).BaseType);
        Assert.Equal(expected, Assert.IsType<string>(literal.Value));
    }

    private static (string Target, int Argc, string Find, string Replacement, string? ReplacementValue)
        CheckRoute(FunctionIR function, Opcode? replacementOpcode)
    {
        var entry = function.Blocks[0];
        var expected = replacementOpcode is { } opcode
            ? new[] { Opcode.LoadArg, Opcode.LoadConst_String, opcode, Opcode.Call, Opcode.Return }
            : new[] { Opcode.LoadArg, Opcode.LoadConst_String, Opcode.Call, Opcode.Return };
        Assert.Equal(expected, entry.Instructions.Select(instruction => instruction.Opcode).ToArray());
        Assert.Equal(0, entry.Instructions[0].Operand0);
        var find = Assert.IsType<string>(entry.Instructions[1].OperandObject);
        Assert.Empty(find);

        var argc = replacementOpcode is null ? 2 : 3;
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Same(entry.Instructions[argc], call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::replaceAll", target);
        Assert.Equal(argc, call.Operand1);

        if (replacementOpcode is null)
            return (target, call.Operand1, find, "omitted", null);

        var replacement = entry.Instructions[2];
        var replacementValue = replacementOpcode == Opcode.LoadConst_String
            ? Assert.IsType<string>(replacement.OperandObject) : null;
        if (replacementOpcode == Opcode.LoadConst_String)
            Assert.Equal("-", replacementValue);
        else
            Assert.Null(replacement.OperandObject);
        return (target, call.Operand1, find, replacement.Opcode.ToString(), replacementValue);
    }
}
