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

public sealed class NumberIsFiniteDecimalTests(ITestOutputHelper output)
{
    private const string Prefix = "C187 Number.isFinite decimal ";

    [Fact]
    public void NumberIsFinite_AcceptsDecimalWithoutCoercion()
    {
        const string source = "function main(input: any): bool { return Number.isFinite(input); }";
        var tokens = new Lexer(source).Tokenize();
        var lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerCount);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var parsed = parser.Parse();
        var parserCount = parser.Diagnostics.Count;
        Assert.Equal(0, parserCount);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parsed);
        var binderCount = binder.Diagnostics.All.Count;
        Assert.Equal(0, binderCount);

        var declaration = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", declaration.Symbol.Name);
        var body = Assert.IsType<BoundBlockStatement>(declaration.Body);
        var statement = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(statement.Value);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(member.Object);
        var numberClass = Assert.IsType<ClassSymbol>(receiver.Symbol);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("Number", numberClass.Name);
        Assert.Equal("Number", method.DeclaringClassName);
        Assert.Equal("isFinite", method.Name);
        Assert.Single(boundCall.Arguments);
        Assert.Same(TsType.Bool, boundCall.Type);

        var ir = new IRGenerator().Generate(bound);
        var function = Assert.Single(ir.Functions, candidate => candidate.Name == "main");
        var instructions = Assert.Single(function.Blocks).Instructions.ToArray();
        Assert.Equal(3, instructions.Length);
        var load = instructions[0];
        var call = instructions[1];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        Assert.Equal(Opcode.Call, call.Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Number::isFinite", target);
        Assert.Equal(1, call.Operand1);
        Assert.Equal(Opcode.Return, instructions[2].Opcode);
        Write(new
        {
            probe = "route",
            lexerCount,
            parserCount,
            binderCount,
            target,
            argc = call.Operand1,
            slot = load.Operand0,
            opcodes = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray()
        });

        var code = BytecodeCompiler.Compile(ir);
        (string Id, TsValue Input, Type Wrapper, TsValueType Tag, bool Expected)[] rows =
        [
            ("int32", TsValue.FromInt32(12), typeof(TsInt32Value), TsValueType.Int32, true),
            ("float32", TsValue.FromFloat32(0.5f), typeof(TsFloat32Value), TsValueType.Float32, true),
            ("float64", TsValue.FromFloat64(-0.25d), typeof(TsFloat64Value), TsValueType.Float64, true),
            ("float64-nan", TsValue.FromFloat64(double.NaN), typeof(TsFloat64Value), TsValueType.Float64, false),
            ("float32-infinity", TsValue.FromFloat32(float.PositiveInfinity), typeof(TsFloat32Value), TsValueType.Float32, false),
            ("numeric-string", TsValue.FromString("12.50"), typeof(TsStringValue), TsValueType.String, false),
            ("int64", TsValue.FromInt64(12L), typeof(TsInt64Value), TsValueType.Int64, false),
            ("decimal-fraction", TsValue.FromDecimal(12.50m), typeof(TsDecimalValue), TsValueType.Decimal, true)
        ];
        foreach (var row in rows)
        {
            Assert.Equal(row.Wrapper, row.Input.GetType());
            Assert.Equal(row.Tag, row.Input.ValueType);
            if (row.Tag == TsValueType.Decimal)
                Assert.Equal(12.50m, Assert.IsType<TsDecimalValue>(row.Input).Value);

            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            var exception = Record.Exception(() => { result = interpreter.Execute(code, "main", [row.Input]); });
            Write(new
            {
                probe = row.Id,
                inputWrapper = row.Input.GetType().Name,
                inputTag = row.Input.ValueType.ToString(),
                resultWrapper = result?.GetType().Name,
                resultTag = result?.ValueType.ToString(),
                expected = row.Expected,
                actual = (result as TsBoolValue)?.Value,
                exception = exception?.GetType().FullName
            });
            Assert.Null(exception);
            var boolean = Assert.IsType<TsBoolValue>(result);
            Assert.Equal(TsValueType.Bool, boolean.ValueType);
            if (row.Tag == TsValueType.Decimal)
                Assert.True(boolean.Value, "C187_NUMBER_ISFINITE_DECIMAL_FRACTION_EXPECTED_TRUE");
            else
                Assert.Equal(row.Expected, boolean.Value);
        }
    }

    private void Write(object observation) => output.WriteLine(Prefix + JsonSerializer.Serialize(observation));
}
