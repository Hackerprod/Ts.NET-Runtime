using System.Globalization;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFindNoMatchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("empty", "", 0, "undefined", "")]
    [InlineData("singleton-miss", "4", 3, "undefined", "4")]
    [InlineData("dense-miss", "1,2,1", 9, "undefined", "1,2,1")]
    [InlineData("first-match", "3,4,3", 3, "int:3", "3")]
    [InlineData("later-match", "1,2,3", 2, "int:2", "1,2")]
    [InlineData("zero-match", "5,0,2", 0, "int:0", "5,0")]
    public void ArrayFind_ReturnsUndefinedWhenNoMatch(string row, string values, int wanted, string expected, string visitsExpected)
    {
        const string source = "function main(values: number[], wanted: number, visits: number[]): any { return values.find((value: number) => { visits.push(value); return value === wanted; }); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var function = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("Array::find", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var array = new TsArray();
        foreach (var text in values.Split(',', StringSplitOptions.RemoveEmptyEntries))
            array.Add(TsValue.FromInt32(int.Parse(text, CultureInfo.InvariantCulture)));
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var visits = new TsArray();
        var result = vm.Execute(bytecode, "main", [new TsArrayValue(array), TsValue.FromInt32(wanted), new TsArrayValue(visits)]);
        var visitsActual = string.Join(",", Enumerable.Range(0, visits.Count).Select(i => Assert.IsType<TsInt32Value>(visits.Get(i)).Value.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(visitsExpected, visitsActual);
        var actual = result switch
        {
            TsVoid => "undefined",
            TsNull => "null",
            TsInt32Value i => FormattableString.Invariant($"int:{i.Value}"),
            _ => $"type:{result?.GetType().FullName ?? "null"}"
        };
        output.WriteLine($"Array.find row={row}; parser/binder=0/0; target=Array::find; argc=2; visits=[{visitsActual}]; expected={expected}; actual={actual}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Array.find result mismatch: expected={expected}; actual={actual}");
    }
}
