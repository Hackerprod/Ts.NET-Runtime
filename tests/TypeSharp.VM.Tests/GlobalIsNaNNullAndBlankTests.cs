using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class GlobalIsNaNNullAndBlankTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("null", null, false)]
    [InlineData("empty", "", false)]
    [InlineData("ecma-whitespace", " \t\uFEFF\u00A0", false)]
    [InlineData("number-text", " 3.25 ", false)]
    [InlineData("infinity-text", "Infinity", false)]
    [InlineData("undefined", null, true)]
    [InlineData("nan", null, true)]
    public void GlobalIsNaN_ClassifiesNullAndBlankPrimitives(string row, string? input, bool expected)
    {
        const string source = "function main(input: any): bool { return isNaN(input); }";
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
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("input", parameter.Name);
        var block = Assert.Single(main.Blocks);
        var ops = block.Instructions;
        Assert.Equal(3, ops.Count);
        Assert.Equal(Opcode.LoadArg, ops[0].Opcode);
        Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(Opcode.Call, ops[1].Opcode);
        var target = Assert.IsType<string>(ops[1].OperandObject);
        Assert.Equal("isNaN", target);
        Assert.Equal(1, ops[1].Operand1);
        Assert.Equal(Opcode.Return, ops[2].Opcode);

        TsValue argument = row switch
        {
            "null" => Assert.IsType<TsNull>(TsValue.Null),
            "undefined" => Assert.IsType<TsVoid>(TsValue.Void),
            "nan" => Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(double.NaN)),
            _ => Assert.IsType<TsStringValue>(TsValue.FromString(Assert.IsType<string>(input)))
        };
        string? inputCategory = argument switch
        {
            TsNull => "null",
            TsVoid => "undefined",
            TsFloat64Value number when double.IsNaN(number.Value) => "NaN",
            _ => null
        };
        int[]? inputUtf16 = null;
        if (argument is TsStringValue text)
        {
            Assert.Equal(input, text.Value);
            inputUtf16 = text.Value.Select(unit => (int)unit).ToArray();
        }
        else
        {
            Assert.NotNull(inputCategory);
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", [argument]); });
        output.WriteLine("C123 global isNaN " + JsonSerializer.Serialize(new
        {
            row, source, parserCount, binderCount, function = main.Name, parameter = parameter.Name,
            blocks = main.Blocks.Count, inputOp = ops[0].Opcode.ToString(), inputArg = ops[0].Operand0,
            opcode = ops[1].Opcode.ToString(), target, argc = ops[1].Operand1, returnOp = ops[2].Opcode.ToString(),
            inputType = argument.GetType().Name, inputCategory, inputUtf16, escaped = escaped?.GetType().FullName,
            actualType = result?.GetType().Name, actual = (result as TsBoolValue)?.Value,
            expectedType = nameof(TsBoolValue), expected
        }));
        Assert.Null(escaped);
        bool actual = Assert.IsType<TsBoolValue>(result).Value;
        Assert.True(expected == actual, $"C123_GLOBAL_ISNAN_RESULT_MISMATCH {row}: expected {expected}; actual {actual}");
    }
}
