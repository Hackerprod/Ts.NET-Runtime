using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFindSparseSlotsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("leading-hole", "H23", "2", "2", "U2")]
    [InlineData("middle-hole", "1H3", "3", "3", "1U3")]
    [InlineData("hole-before-undefined", "1H2U3", "U", "U", "1U")]
    [InlineData("dense-match", "123", "2", "2", "12")]
    [InlineData("early-before-hole", "1H3", "1", "1", "1")]
    [InlineData("explicit-undefined", "1U3", "U", "U", "1U")]
    public void ArrayFind_VisitsSparseSlots(string row, string input, string wanted, string expected, string visitsExpected)
    {
        const string source = "function main(values: any[], wanted: any, visits: any[]): any { return values.find((value: any) => { visits.push(value); return value === wanted; }); }";
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

        static TsValue Value(char c) => c == 'U' ? TsValue.Void : TsValue.FromInt32(c - '0');
        static char Token(TsValue? value) => value is TsVoid ? 'U' : (char)('0' + Assert.IsType<TsInt32Value>(value).Value);
        var array = new TsArray();
        foreach (var slot in input)
            if (slot == 'H') array.SetHole(array.Count); else array.Add(Value(slot));
        Assert.Equal(input, Snapshot(array));
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", [new TsArrayValue(array), Value(wanted[0]), new TsArrayValue(visits)]);
        Assert.Equal(input, Snapshot(array));
        Assert.Equal(expected, Token(result).ToString());
        var actual = Snapshot(visits);
        output.WriteLine($"Array.find sparse row={row}; parser/binder=0/0; target=Array::find; argc=2; receiver={input}; result={expected}; visitsExpected={visitsExpected}; visitsActual={actual}");
        Assert.True(visitsExpected == actual, $"Array.find sparse visits mismatch: expected={visitsExpected}; actual={actual}");
        static string Snapshot(TsArray a) => string.Concat(Enumerable.Range(0, a.Count).Select(i => a.IsHole(i) ? 'H' : Token(a.Get(i))));
    }
}
