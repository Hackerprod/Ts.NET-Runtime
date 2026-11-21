using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringRepeatOmittedCountTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted", "", 1, "")]
    [InlineData("explicit-undefined", "undefined", 2, "")]
    [InlineData("explicit-zero", "0", 2, "")]
    [InlineData("explicit-positive", "2", 2, "abab")]
    public void StringRepeat_DefaultsOmittedCountToZero(string row, string count, int argc, string expected)
    {
        var source = $"function main(s: string): string {{ return s.repeat({count}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::repeat", target);
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        TsValue[] args = [TsValue.FromString("ab")];
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var actual = error is null ? Assert.IsType<TsStringValue>(result).Value : null;
        var display = error is null ? JsonSerializer.Serialize(actual) : $"throw:{error.GetType().Name}";
        output.WriteLine($"String.repeat row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; expected={JsonSerializer.Serialize(expected)}; actual={display}");
        Assert.True(error is null, $"String.repeat VM execution failed: row={row}; actual={display}");
        Assert.Equal(expected, actual);
    }
}
