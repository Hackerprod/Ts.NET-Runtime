using System.Globalization;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayPopEmptyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("empty", "", "U", "")]
    [InlineData("drained", "7", "7,U", "")]
    [InlineData("dense", "17", "7", "1")]
    [InlineData("explicit-null", "1N", "N", "1")]
    [InlineData("explicit-undefined", "1U", "U", "1")]
    [InlineData("trailing-hole", "1H", "U", "1")]
    [InlineData("retained-hole", "H7", "7", "H")]
    public void ArrayPop_EmptyReturnsUndefined(string row, string initial, string expected, string remaining)
    {
        const string source = "function main(a: any[]): any { return a.pop(); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::pop", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(1, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var arr = new TsArray();
        foreach (char slot in initial)
        {
            switch (slot)
            {
                case 'H': arr.SetHole(arr.Count); break;
                case 'N': arr.Add(TsValue.Null); break;
                case 'U': arr.Add(TsValue.Void); break;
                default: arr.Add(TsValue.FromInt32(slot - '0')); break;
            }
        }

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var receiver = new TsArrayValue(arr);
        var expectedPops = expected.Split(',');
        var actual = string.Join(",", expectedPops.Select(_ => Tag(vm.Execute(bytecode, "main", [receiver]))));
        var actualRemaining = string.Concat(Enumerable.Range(0, arr.Count)
            .Select(i => arr.IsHole(i) ? "H" : Tag(arr.Get(i))));

        output.WriteLine($"Array.pop row={row}; parser/binder=0/0; target={call.OperandObject}; argc={call.Operand1}; result expected={expected}; actual={actual}; remaining expected=[{remaining}]; actual=[{actualRemaining}]");
        Assert.Equal(remaining, actualRemaining);
        Assert.True(expected == actual, $"Array.pop result mismatch: expected={expected}; actual={actual}");
    }

    private static string Tag(TsValue? value) => value switch
    {
        null => "<no VM value>",
        TsVoid => "U",
        TsNull => "N",
        TsInt32Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        _ => $"<{value.GetType().Name}>"
    };
}
