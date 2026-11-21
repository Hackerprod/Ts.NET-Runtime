using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayForEachUndefinedResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("traversal-control", false, 0)]
    [InlineData("strict-undefined", true, 1)]
    public void ArrayForEach_VisitsEntriesAndReturnsUndefined(string row, bool checkReturn, int expectedTag)
    {
        const string source = """
            function main(values: any[], visits: any[], checkReturn: boolean): int32 {
                let result: any = values.forEach((value: any, i: any) => { visits.push(value); });
                if (checkReturn) {
                    if (result === undefined) {
                        if (result === null) return 3;
                        return 1;
                    }
                    if (result === null) return 2;
                    return 3;
                }
                return 0;
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

        var values = new TsArray();
        values.Add(TsValue.FromInt32(2));
        values.Add(TsValue.FromInt32(4));
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", [new TsArrayValue(values), new TsArrayValue(visits), TsValue.FromBool(checkReturn)]);
        int[] expectedVisits = [2, 4];
        var actualVisits = Enumerable.Range(0, visits.Count)
            .Select(i => Assert.IsType<TsInt32Value>(visits.Get(i)).Value).ToArray();
        output.WriteLine($"Array.forEach row={row}; parser/binder=0/0; target=Array::forEach; argc=2; expectedVisits=[{string.Join(",", expectedVisits)}]; actualVisits=[{string.Join(",", actualVisits)}]; expectedTag={expectedTag}; actualTag={result}");
        Assert.Equal(expectedVisits.Length, visits.Count);
        Assert.Equal(expectedVisits, actualVisits);
        Assert.Equal(expectedTag, Assert.IsType<TsInt32Value>(result).Value);
    }
}
