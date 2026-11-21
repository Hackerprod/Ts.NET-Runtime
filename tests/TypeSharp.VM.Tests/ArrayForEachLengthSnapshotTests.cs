using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayForEachLengthSnapshotTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("append", 0, "12", "129", "12")]
    [InlineData("sparse-live", 1, "1HH4", "12H9", "129")]
    [InlineData("shrink", 2, "123", "1", "1")]
    public void ArrayForEach_SnapshotsLengthButReadsLiveEntries(string row, int mode, string input, string expectedReceiver, string expectedVisits)
    {
        const string source = """
            function main(values: any[], visits: any[], mode: any): void {
                values.forEach((value: any, i: any) => {
                    visits.push(value);
                    if (i === 0) {
                        if (mode === 0) { values.push(9); }
                        if (mode === 1) { values[1] = 2; values[3] = 9; }
                        if (mode === 2) { values.pop(); values.pop(); }
                    }
                });
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("Array::forEach", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var array = new TsArray();
        foreach (var entry in input)
        {
            if (entry == 'H')
                array.SetHole(array.Count);
            else
                array.Add(TsValue.FromInt32(entry - '0'));
        }
        Assert.Equal(input, Snapshot(array));

        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        vm.Execute(bytecode, "main", [new TsArrayValue(array), new TsArrayValue(visits), TsValue.FromInt32(mode)]);
        var expected = $"{expectedReceiver}/{expectedVisits}";
        var actual = $"{Snapshot(array)}/{Snapshot(visits)}";
        output.WriteLine($"Array.forEach length row={row}; parser/binder=0/0; target=Array::forEach; argc=2; receiverBefore={input}; expected={expected}; actual={actual}");
        Assert.True(expected == actual, $"Array.forEach length snapshot mismatch: expected={expected}; actual={actual}");

        static string Snapshot(TsArray a) => string.Concat(Enumerable.Range(0, a.Count)
            .Select(i => a.IsHole(i) ? 'H' : (char)('0' + Assert.IsType<TsInt32Value>(a.Get(i)).Value)));
    }
}
