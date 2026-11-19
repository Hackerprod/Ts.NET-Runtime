using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class JsonStringifyNonFiniteTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("root", "f64", "NaN", "null")]
    [InlineData("root", "f64", "Infinity", "null")]
    [InlineData("root", "f64", "-Infinity", "null")]
    [InlineData("array", "f64", "Infinity", "[1,null,2]")]
    [InlineData("object", "f64", "NaN", "{\"reading\":null}")]
    [InlineData("root", "f32", "NaN", "null")]
    [InlineData("root", "f32", "Infinity", "null")]
    [InlineData("object", "f32", "-Infinity", "{\"reading\":null}")]
    [InlineData("root", "f64", "-3.5", "-3.5")]
    [InlineData("root", "f32", "1.5", "1.5")]
    [InlineData("array", "f64", "2.5", "[1,2.5,2]")]
    [InlineData("object", "f64", "8", "{\"reading\":8}")]
    [InlineData("root", "f64", "null", "null")]
    public void JsonStringify_NumberSerialization(string shape, string rep, string input, string expected)
    {
        var expression = shape switch
        {
            "root" => "n",
            "array" => "[1, n, 2]",
            "object" => "{ reading: n }",
            _ => throw new InvalidOperationException(shape)
        };
        var source = $"function main(n: any): string {{ return JSON.stringify({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("JSON::stringify", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(1, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        var numeric = input switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            "null" => 0d,
            _ => double.Parse(input, System.Globalization.CultureInfo.InvariantCulture)
        };
        TsValue value = input == "null" ? TsValue.Null
            : rep == "f32" ? TsValue.FromFloat32((float)numeric) : TsValue.FromFloat64(numeric);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = vm.Execute(bytecode, "main", [value]); });
        var actual = (result as TsStringValue)?.Value;
        var outcome = error is null ? actual ?? $"<{result?.GetType().Name ?? "no value"}>"
            : $"{error.GetType().Name}: {error.GetBaseException().Message}";
        output.WriteLine($"JSON.stringify shape={shape} rep={rep} input={input}; parser/binder=0/0; target={call.OperandObject}; argc={call.Operand1}; expected={expected}; actual={outcome}");
        Assert.True(error is null && result is TsStringValue && actual == expected,
            $"JSON.stringify value mismatch: expected={expected}; actual={outcome}");
    }
}
