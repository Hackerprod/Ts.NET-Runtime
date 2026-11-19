using System.Globalization;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySpliceOmittedStartTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omit-dense", "", 1, "123", "123", "")]
    [InlineData("omit-sparse", "", 1, "1HUN", "1HUN", "")]
    [InlineData("omit-empty", "", 1, "", "", "")]
    [InlineData("start-only", "1", 2, "123", "1", "23")]
    [InlineData("explicit-undefined", "undefined", 2, "1HUN", "", "1HUN")]
    [InlineData("explicit-zero-count", "0, 0", 3, "123", "123", "")]
    [InlineData("replace", "1, 1, 7", 4, "123", "173", "2")]
    public void ArraySplice_OmittedStartDoesNotDelete(
        string row, string arguments, int argc, string initial, string remaining, string removed)
    {
        var source = $"function main(a: any[]): any[] {{ return a.splice({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::splice", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

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

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsArrayValue>(vm.Execute(bytecode, "main", [new TsArrayValue(arr)])).Value;
        Assert.NotSame(arr, result);

        var actualRemaining = Snapshot(arr);
        var actualRemoved = Snapshot(result);
        output.WriteLine($"Array.splice row={row}; parser/binder=0/0; target={call.OperandObject}; argc={call.Operand1}; remaining=[{actualRemaining}]; removed=[{actualRemoved}]");
        Assert.True((remaining, removed) == (actualRemaining, actualRemoved),
            $"Array.splice state mismatch: expected remaining=[{remaining}], removed=[{removed}]; actual remaining=[{actualRemaining}], removed=[{actualRemoved}]");
    }

    private static string Snapshot(TsArray array) => string.Concat(Enumerable.Range(0, array.Count)
        .Select(i => array.IsHole(i) ? "H" : array.Get(i) switch
        {
            TsVoid => "U",
            TsNull => "N",
            TsInt32Value n => n.Value.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Unexpected array slot type")
        }));
}
