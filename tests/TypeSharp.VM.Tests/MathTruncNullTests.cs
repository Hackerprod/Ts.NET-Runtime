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

public sealed class MathTruncNullTests(ITestOutputHelper output)
{
    [Fact]
    public void Trunc_ExplicitNullReturnsPositiveZero()
    {
        const string source = "function main(value: any): number { return Math.trunc(value); }";
        var tokens = new Lexer(source, "c194-math-trunc-null.ts").Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(TsType.Number, function.Symbol.Type);
        var parameter = Assert.Single(function.Symbol.Parameters);
        Assert.Equal("value", parameter.Name);
        Assert.Same(TsType.Any, parameter.Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, boundCall.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        Assert.Equal("Math", Assert.IsType<ClassSymbol>(receiver.Symbol).Name);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("Math", method.DeclaringClassName);
        Assert.Equal("trunc", method.Name);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(boundCall.Arguments));
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(argument.Symbol));
        Assert.Same(TsType.Any, argument.Type);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        var entry = main.Blocks[0];
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return },
            entry.Instructions.Select(instruction => instruction.Opcode).ToArray());
        Assert.Equal(0, entry.Instructions[0].Operand0);
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Same(entry.Instructions[1], call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Math::trunc", target);
        var argc = call.Operand1;
        Assert.Equal(1, argc);
        var module = BytecodeCompiler.Compile(ir);

        (string Name, TsValue Input, long? ExpectedBits)[] rows =
        [
            ("explicit-null", TsValue.Null, 0L),
            ("explicit-undefined", TsValue.Void, null),
            ("negative-fraction", TsValue.FromFloat64(-1.75d), unchecked((long)0xBFF0000000000000UL)),
            ("negative-zero", TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue)), long.MinValue),
            ("false", TsValue.FromBool(false), 0L),
            ("decimal-string", TsValue.FromString("2.75"), 0x4000000000000000L),
        ];

        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue[] operands = [row.Input];
            TsValue? value = null;
            Exception? error = null;
            try
            {
                value = interpreter.Execute(module, "main", operands);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            var number = value as TsFloat64Value;
            var isNaN = number is not null && double.IsNaN(number.Value);
            long? bits = number is null || isNaN ? null : BitConverter.DoubleToInt64Bits(number.Value);
            var ok = error is null && number is { ValueType: TsValueType.Float64 }
                && (row.ExpectedBits is null ? isNaN : bits == row.ExpectedBits);
            output.WriteLine("C194 Math.trunc " + JsonSerializer.Serialize(new
            {
                row = row.Name,
                lexerErrors,
                parserDiagnostics = parser.Diagnostics.Count(),
                binderDiagnostics = binder.Diagnostics.All.Count(),
                route = "LoadArg(0),Call(1),Return",
                target,
                argc,
                inputWrapper = row.Input.GetType().Name,
                inputTag = row.Input.ValueType.ToString(),
                inputBits = row.Input is TsFloat64Value inputNumber
                    ? Hex(BitConverter.DoubleToInt64Bits(inputNumber.Value))
                    : null,
                expected = row.ExpectedBits is long expectedBits ? Hex(expectedBits) : "NaN",
                wrapper = value?.GetType().Name,
                tag = value?.ValueType.ToString(),
                observed = isNaN ? "NaN" : bits is long observedBits ? Hex(observedBits) : null,
                exception = error?.GetType().FullName,
                exceptionMessage = error?.Message,
                ok
            }));
            if (!ok)
                mismatches.Add(row.Name);
        }

        if (mismatches.Count != 0)
            Assert.Fail($"C194_SEMANTIC_MISMATCHES {string.Join(",", mismatches)}");
    }

    private static string Hex(long bits) => bits.ToString("X16", CultureInfo.InvariantCulture);
}
