using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class GlobalIsFiniteBlankStringTests(ITestOutputHelper output)
{
    private const string Source = "function main(input: string): bool { return isFinite(input); }";

    [Theory]
    [InlineData("empty", "", true)]
    [InlineData("ascii-blank", " \t\v\f\r\n", true)]
    [InlineData("nbsp", "\u00a0", true)]
    [InlineData("bom", "\ufeff", true)]
    [InlineData("line-separator", "\u2028", true)]
    [InlineData("paragraph-separator", "\u2029", true)]
    [InlineData("mixed-blank", "\t\u00a0\ufeff\u2028\u2029", true)]
    [InlineData("nel-control", "\u0085", false)]
    [InlineData("malformed-control", "foo", false)]
    [InlineData("decimal-control", "42", true)]
    [InlineData("signed-zero-control", "-0", true)]
    [InlineData("infinity-control", "Infinity", false)]
    public void GlobalIsFinite_ClassifiesOnlyEcmaBlankStrings(string id, string input, bool expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal("input", Assert.Single(main.Parameters).Name);
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("isFinite", target);
        Assert.Equal(1, call.Operand1);
        var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
        int index = block.Instructions.IndexOf(call);
        Assert.True(index >= 1);
        var load = block.Instructions[index - 1];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);

        var code = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue[] args = [new TsStringValue(input)];
        var text = Assert.IsType<TsStringValue>(Assert.Single(args));
        Assert.True(string.Equals(input, text.Value, StringComparison.Ordinal));
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(code, "main", args); });
        Assert.Null(escaped);
        var actual = Assert.IsType<TsBoolValue>(result).Value;
        output.WriteLine("C102 global isFinite row " + JsonSerializer.Serialize(new
        {
            id, source = Source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            target, opcode = call.Opcode.ToString(), argc = call.Operand1, argumentIndex = load.Operand0,
            inputType = text.GetType().Name, codeUnits = text.Value.Select(v => ((int)v).ToString("x4")),
            exception = escaped?.GetType().FullName, actualType = result!.GetType().Name, actual, expected
        }));
        Assert.True(expected == actual,
            $"C102_GLOBAL_ISFINITE_RESULT_MISMATCH {id}: expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual)}");
    }
}
