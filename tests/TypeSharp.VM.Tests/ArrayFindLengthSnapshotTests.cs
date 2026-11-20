using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFindLengthSnapshotTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("append-match", "12", 1, 3, 3, "123", "U/12")]
    [InlineData("append-nonmatch", "12", 1, 3, 4, "123", "U/12")]
    [InlineData("in-bounds-match", "12", 1, 3, 2, "123", "2/12")]
    [InlineData("early-match", "12", 1, 3, 1, "123", "1/1")]
    [InlineData("no-append", "12", 0, 3, 2, "12", "2/12")]
    [InlineData("empty", "", 1, 3, 3, "", "U/")]
    public void ArrayFind_SnapshotsLengthBeforeCallback(string row, string input, int trigger, int appended, int wanted, string receiverExpected, string expected)
    {
        const string source = "function main(values: any[], trigger: any, appended: any, wanted: any, visits: any[]): any { return values.find((value: any) => { visits.push(value); if (value === trigger) { values.push(appended); } return value === wanted; }); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("Array::find", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var array = new TsArray();
        foreach (var c in input)
            array.Add(TsValue.FromInt32(c - '0'));
        Assert.Equal(input, Snapshot(array));
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", [new TsArrayValue(array), TsValue.FromInt32(trigger), TsValue.FromInt32(appended), TsValue.FromInt32(wanted), new TsArrayValue(visits)]);
        var receiverActual = Snapshot(array);
        Assert.Equal(receiverExpected, receiverActual);
        var actual = $"{Token(result)}/{Snapshot(visits)}";
        output.WriteLine($"Array.find length row={row}; parser/binder=0/0; target=Array::find; argc=2; receiverBefore={input}; receiverAfter={receiverActual}; expected={expected}; actual={actual}");
        Assert.True(expected == actual, $"Array.find length snapshot mismatch: expected={expected}; actual={actual}");

        static char Token(TsValue? value) => value is TsVoid ? 'U' : (char)('0' + Assert.IsType<TsInt32Value>(value).Value);
        static string Snapshot(TsArray a) => string.Concat(Enumerable.Range(0, a.Count).Select(i => { Assert.False(a.IsHole(i)); return Token(a.Get(i)); }));
    }
}
