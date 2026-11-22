using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class GlobalIsFiniteNullTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("null", true)]
    [InlineData("undefined", false)]
    [InlineData("false", true)]
    [InlineData("zero", true)]
    [InlineData("infinity", false)]
    [InlineData("blank", true)]
    [InlineData("malformed", false)]
    public void GlobalIsFinite_ClassifiesNullAndControls(string row, bool expected)
    {
        const string source = "function main(input: any): bool { return isFinite(input); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.False(main.IsAsync);
        Assert.False(main.IsGenerator);
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("input", parameter.Name);
        Assert.IsType<TsAnyType>(parameter.Type);
        Assert.False(parameter.IsRest);
        Assert.Equal(TsType.Bool, main.ReturnType);

        var ops = Assert.Single(main.Blocks).Instructions;
        Assert.Equal(3, ops.Count);
        Assert.Equal(Opcode.LoadArg, ops[0].Opcode);
        Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(Opcode.Call, ops[1].Opcode);
        var target = Assert.IsType<string>(ops[1].OperandObject);
        Assert.Equal("isFinite", target);
        Assert.Equal(1, ops[1].Operand1);
        Assert.Equal(Opcode.Return, ops[2].Opcode);
        var route = new
        {
            load = ops[0].Opcode.ToString(), slot = ops[0].Operand0,
            call = ops[1].Opcode.ToString(), target, argc = ops[1].Operand1,
            ret = ops[2].Opcode.ToString()
        };

        TsValue argument;
        TsValueType expectedTag;
        object input;
        switch (row)
        {
            case "null":
                var nullValue = Assert.IsType<TsNull>((TsValue)TsValue.Null);
                Assert.Same(TsValue.Null, nullValue);
                argument = nullValue;
                expectedTag = TsValueType.Null;
                input = "null";
                break;
            case "undefined":
                var undefined = Assert.IsType<TsVoid>((TsValue)TsValue.Void);
                Assert.Same(TsValue.Void, undefined);
                argument = undefined;
                expectedTag = TsValueType.Void;
                input = "undefined";
                break;
            case "false":
                var boolean = Assert.IsType<TsBoolValue>(TsValue.FromBool(false));
                Assert.False(boolean.Value);
                argument = boolean;
                expectedTag = TsValueType.Bool;
                input = boolean.Value;
                break;
            case "zero":
                var integer = Assert.IsType<TsInt32Value>(TsValue.FromInt32(0));
                Assert.Equal(0, integer.Value);
                argument = integer;
                expectedTag = TsValueType.Int32;
                input = integer.Value;
                break;
            case "infinity":
                var infinity = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(double.PositiveInfinity));
                Assert.True(double.IsPositiveInfinity(infinity.Value));
                argument = infinity;
                expectedTag = TsValueType.Float64;
                input = "+Infinity";
                break;
            case "blank":
            case "malformed":
                string expectedInput = row == "blank" ? "" : "x";
                var text = Assert.IsType<TsStringValue>(TsValue.FromString(expectedInput));
                Assert.Equal(expectedInput, text.Value);
                argument = text;
                expectedTag = TsValueType.String;
                input = text.Value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown fixture row.");
        }
        Assert.Equal(expectedTag, argument.ValueType);

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", [argument]); });
        output.WriteLine("C127 global isFinite " + JsonSerializer.Serialize(new
        {
            row, source, parserCount, binderCount,
            function = main.Name, parameter = parameter.Name, parameterType = parameter.Type.Name,
            isRest = parameter.IsRest, returnType = main.ReturnType.Name, blocks = main.Blocks.Count,
            route, input, inputType = argument.GetType().Name, inputTag = argument.ValueType.ToString(),
            isNullSingleton = ReferenceEquals(argument, TsValue.Null),
            isVoidSingleton = ReferenceEquals(argument, TsValue.Void),
            escaped = escaped?.GetType().FullName, actualType = result?.GetType().Name,
            actualTag = result?.ValueType.ToString(), expectedType = nameof(TsBoolValue),
            expectedTag = nameof(TsValueType.Bool), expected, actual = (result as TsBoolValue)?.Value
        }));
        Assert.Null(escaped);
        var value = Assert.IsType<TsBoolValue>(result);
        Assert.Equal(TsValueType.Bool, value.ValueType);
        if (row == "null")
            Assert.True(expected == value.Value,
                $"C127_GLOBAL_ISFINITE_NULL_RESULT_MISMATCH row={row}; expected={expected}; actual={value.Value}");
        else
            Assert.Equal(expected, value.Value);
    }
}
