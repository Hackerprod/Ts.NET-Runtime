using System.Buffers.Binary;
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

public sealed class StringSplitUndefinedDecimalTextLimitTests(ITestOutputHelper output)
{
    private const string Target = "String::split";
    private const string Prefix = "C239_STRING_SPLIT_UNDEFINED_DECIMAL_TEXT_LIMIT";

    [Fact]
    public void Split_UsesDecimalTextLimitWithUndefinedSeparator()
    {
        var supplied = CompileRoute("undefined-supplied", "c239-undefined-supplied.ts",
            "function main(text: string, limit: any): string[] { return text.split(undefined, limit); }",
            [("text", TsType.String), ("limit", TsType.Any)], [0, null, 1],
            "12000000000B120100000073000000000300000075");
        var omitted = CompileRoute("undefined-omitted", "c239-undefined-omitted.ts",
            "function main(text: string): string[] { return text.split(undefined); }",
            [("text", TsType.String)], [0, null],
            "12000000000B73000000000200000075");
        var comma = CompileRoute("comma-supplied", "c239-comma-supplied.ts",
            "function main(text: string, separator: string, limit: any): string[] { return text.split(separator, limit); }",
            [("text", TsType.String), ("separator", TsType.String), ("limit", TsType.Any)], [0, 1, 2],
            "12000000001201000000120200000073000000000300000075");

        const string Whole = "aundefinedb";
        var rows = new (string Id, Route Route, object[] Inputs, string[] Expected)[]
        {
            ("text-zero-nonempty", supplied, [Whole, "0.9"], []),
            ("text-zero-empty", supplied, ["", "0.9"], []),
            ("text-one", supplied, [Whole, "1.9"], [Whole]),
            ("number-zero", supplied, [Whole, 0.9d], []),
            ("boolean-false", supplied, [Whole, false], []),
            ("boolean-true", supplied, [Whole, true], [Whole]),
            ("omitted-limit", omitted, [Whole], [Whole]),
            ("explicit-undefined-limit", supplied, [Whole, TsValue.Void], [Whole]),
            ("comma-zero", comma, ["a,b", ",", "0.9"], []),
            ("comma-one", comma, ["a,b", ",", "1.9"], ["a"])
        };
        var observed = rows.Select(row => Measure(row.Id, row.Route, row.Inputs)).ToArray();

        for (int i = 2; i < rows.Length; i++)
            Assert.True(rows[i].Expected.SequenceEqual(observed[i], StringComparer.Ordinal),
                $"Control {rows[i].Id}: expected {JsonSerializer.Serialize(rows[i].Expected)}, observed {JsonSerializer.Serialize(observed[i])}");

        var expectedWitness = new[] { rows[0].Expected, rows[1].Expected };
        var observedWitness = new[] { observed[0], observed[1] };
        Assert.True(expectedWitness.Zip(observedWitness).All(pair =>
                pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal)),
            $"{Prefix}_RESULT expected {JsonSerializer.Serialize(expectedWitness)}, observed {JsonSerializer.Serialize(observedWitness)}");
    }

    private Route CompileRoute(string route, string fileName, string source,
        (string Name, TsType Type)[] expectedParameters, int?[] loads, string expectedHex)
    {
        var tokens = new Lexer(source, fileName).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        CheckStringArrayType(function.Symbol.Type);
        var parameters = function.Symbol.Parameters;
        Assert.Equal(expectedParameters.Select(parameter => parameter.Name), parameters.Select(parameter => parameter.Name));
        for (int i = 0; i < expectedParameters.Length; i++)
            Assert.Same(expectedParameters[i].Type, parameters[i].Type);

        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        CheckStringArrayType(call.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal("split", method.Name);
        CheckStringArrayType(method.Type);
        CheckParameterUse(member.Object, parameters[0], TsType.String);
        Assert.Equal(loads.Length - 1, call.Arguments.Count);
        for (int i = 1; i < loads.Length; i++)
        {
            var argument = call.Arguments[i - 1];
            if (loads[i] is int parameterIndex)
                CheckParameterUse(argument, parameters[parameterIndex], expectedParameters[parameterIndex].Type);
            else
            {
                var literal = Assert.IsType<BoundLiteralExpression>(argument);
                Assert.Null(literal.Value);
                Assert.Same(TsType.Undefined, literal.Type);
            }
        }

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
        CheckStringArrayType(irMain.ReturnType);
        Assert.Equal(expectedParameters.Select(parameter => parameter.Name), irMain.Parameters.Select(parameter => parameter.Name));
        for (int i = 0; i < expectedParameters.Length; i++)
            Assert.Same(expectedParameters[i].Type, irMain.Parameters[i].Type);

        var instructions = Assert.Single(irMain.Blocks).Instructions;
        Assert.Equal(loads.Length + 2, instructions.Count);
        for (int i = 0; i < loads.Length; i++)
            if (loads[i] is int parameterIndex)
                CheckInstruction(instructions[i], Opcode.LoadArg, parameterIndex);
            else
                CheckInstruction(instructions[i], Opcode.LoadConst_Void);
        CheckInstruction(instructions[^2], Opcode.Call, argc: loads.Length, target: Target);
        CheckInstruction(instructions[^1], Opcode.Return);

        var compiled = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(stream.Length, stream.Position);
        BytecodeVerifier.Verify(restored);

        var compiledMain = Assert.Single(compiled.Functions);
        var restoredMain = Assert.Single(restored.Functions);
        Assert.Equal(irMain.Name, compiledMain.Name);
        Assert.Equal(irMain.Name, restoredMain.Name);
        Assert.Equal(expectedParameters.Length, compiledMain.ParameterCount);
        Assert.Equal(expectedParameters.Length, restoredMain.ParameterCount);
        var compiledHex = Convert.ToHexString(compiledMain.Instructions);
        var restoredHex = Convert.ToHexString(restoredMain.Instructions);
        Assert.Equal(expectedHex, compiledHex);
        Assert.Equal(expectedHex, restoredHex);
        Assert.Equal(Target, Assert.Single(compiledMain.StringConstants));
        Assert.Equal(Target, Assert.Single(restoredMain.StringConstants));

        var bytes = restoredMain.Instructions;
        int offset = 0;
        foreach (int? load in loads)
        {
            if (load is int parameterIndex)
            {
                Assert.Equal(Opcodes.LoadArg, bytes[offset++]);
                Assert.Equal(parameterIndex, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
                offset += 4;
            }
            else
                Assert.Equal(Opcodes.LoadConstVoid, bytes[offset++]);
        }
        Assert.Equal(Opcodes.Call, bytes[offset++]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(0, targetIndex);
        var target = restoredMain.StringConstants[targetIndex];
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
        Assert.Equal(loads.Length, argc);
        offset += 8;
        Assert.Equal(Opcodes.Return, bytes[offset++]);
        Assert.Equal(bytes.Length, offset);

        Write(new
        {
            route,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            target,
            argc,
            compiledHex,
            restoredHex
        });
        return new Route(route, restored, restoredMain.Name, restoredMain.ParameterCount);
    }

    private string[] Measure(string row, Route route, object[] specifications)
    {
        var arguments = specifications.Select(specification => specification switch
        {
            string text => TsValue.FromString(text),
            double numeric => TsValue.FromFloat64(numeric),
            bool boolean => TsValue.FromBool(boolean),
            TsVoid => TsValue.Void,
            _ => throw new InvalidOperationException("Unexpected argument specification")
        }).ToArray();
        Assert.Equal(route.ParameterCount, arguments.Length);
        var inputs = specifications.Zip(arguments, ObserveInput).ToArray();
        var result = Assert.IsType<TsArrayValue>(
            new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, arguments));
        Assert.Equal(TsValueType.Array, result.ValueType);
        var array = result.Value;
        var values = new string[array.Count];
        var items = new object[array.Count];
        bool dense = true;
        for (int i = 0; i < array.Count; i++)
        {
            bool hole = array.IsHole(i);
            dense &= !hole;
            Assert.False(hole);
            var item = Assert.IsType<TsStringValue>(array.Get(i));
            Assert.Equal(TsValueType.String, item.ValueType);
            values[i] = item.Value;
            items[i] = Observe(item, item.Value);
        }

        Write(new
        {
            row,
            route = route.Id,
            inputs,
            resultWrapper = result.GetType().Name,
            resultTag = result.ValueType.ToString(),
            count = array.Count,
            dense,
            items
        });
        return values;
    }

    private static object ObserveInput(object specification, TsValue value)
    {
        switch (specification)
        {
            case string text:
                var actualText = Assert.IsType<TsStringValue>(value);
                Assert.Equal(TsValueType.String, actualText.ValueType);
                Assert.True(string.Equals(text, actualText.Value, StringComparison.Ordinal));
                return Observe(actualText, actualText.Value);
            case double numeric:
                var actualNumeric = Assert.IsType<TsFloat64Value>(value);
                Assert.Equal(TsValueType.Float64, actualNumeric.ValueType);
                Assert.Equal(numeric, actualNumeric.Value);
                return Observe(actualNumeric, actualNumeric.Value);
            case bool boolean:
                var actualBoolean = Assert.IsType<TsBoolValue>(value);
                Assert.Equal(TsValueType.Bool, actualBoolean.ValueType);
                Assert.Equal(boolean, actualBoolean.Value);
                return Observe(actualBoolean, actualBoolean.Value);
            case TsVoid:
                var actualVoid = Assert.IsType<TsVoid>(value);
                Assert.Same(TsValue.Void, actualVoid);
                Assert.Equal(TsValueType.Void, actualVoid.ValueType);
                return Observe(actualVoid, "undefined");
            default:
                throw new InvalidOperationException("Unexpected argument specification");
        }
    }

    private static void CheckStringArrayType(TsType type)
    {
        var array = Assert.IsType<TsArrayType>(type);
        Assert.Same(TsType.String, array.ElementType);
        Assert.False(array.IsReadonly);
    }

    private static void CheckParameterUse(BoundNode node, ParameterSymbol parameter, TsType type)
    {
        var variable = Assert.IsType<BoundVariableExpression>(node);
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(variable.Symbol));
        Assert.Same(type, variable.Type);
    }

    private static void CheckInstruction(Instruction instruction, Opcode opcode, int operand = 0, int argc = 0,
        string? target = null)
    {
        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(operand, instruction.Operand0);
        Assert.Equal(argc, instruction.Operand1);
        if (target is null)
            Assert.Null(instruction.OperandObject);
        else
            Assert.Equal(target, Assert.IsType<string>(instruction.OperandObject));
    }

    private static object Observe(TsValue value, object payload) =>
        new { wrapper = value.GetType().Name, tag = value.ValueType.ToString(), value = payload };

    private void Write(object observation) => output.WriteLine(Prefix + " " + JsonSerializer.Serialize(observation));

    private sealed record Route(string Id, BytecodeModule Module, string Main, int ParameterCount);
}
