using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MapForEachUndefinedResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("traversal-control", false, 0)]
    [InlineData("strict-undefined", true, 1)]
    public void MapForEach_VisitsEntriesAndReturnsUndefined(string row, bool checkReturn, int expectedTag)
    {
        const string source = """
            function main(values: Map<string, int32>, visits: any[], checkReturn: boolean): int32 {
                let result: any = values.forEach((value: any, key: any) => {
                    visits.push(key);
                    visits.push(value);
                    return 7;
                });
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
        Assert.Equal("Map::forEach", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var values = new TsMap();
        values.Set(TsValue.FromString("b"), TsValue.FromInt32(2));
        values.Set(TsValue.FromString("a"), TsValue.FromInt32(4));
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", [new TsMapValue(values), new TsArrayValue(visits), TsValue.FromBool(checkReturn)]);
        Assert.Equal(4, visits.Count);
        var firstKey = Assert.IsType<TsStringValue>(visits.Get(0)).Value;
        var firstValue = Assert.IsType<TsInt32Value>(visits.Get(1)).Value;
        var secondKey = Assert.IsType<TsStringValue>(visits.Get(2)).Value;
        var secondValue = Assert.IsType<TsInt32Value>(visits.Get(3)).Value;
        var actualTag = Assert.IsType<TsInt32Value>(result).Value;
        output.WriteLine($"Map.forEach row={row}; parser/binder=0/0; target=Map::forEach; argc=2; expectedVisits=[b,2,a,4]; actualVisits=[{firstKey},{firstValue},{secondKey},{secondValue}]; expectedTag={expectedTag}; actualTag={actualTag}");
        Assert.Equal(("b", 2, "a", 4), (firstKey, firstValue, secondKey, secondValue));
        Assert.Equal(expectedTag, actualTag);
    }
}
