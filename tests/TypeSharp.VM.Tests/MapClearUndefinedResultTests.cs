using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MapClearUndefinedResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("mutation-only-control", false, 0)]
    [InlineData("strict-undefined", true, 1)]
    public void MapClear_EmptiesReceiverAndReturnsUndefined(string row, bool checkReturn, int expectedTag)
    {
        const string source = """
            function main(values: Map<string, int32>, checkReturn: boolean): int32 {
                let result: any = values.clear();
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
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Map::clear", target);
        Assert.Equal(1, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var values = new TsMap();
        var oldKey = TsValue.FromString("old");
        var newKey = TsValue.FromString("new");
        values.Set(oldKey, TsValue.FromInt32(7));
        var oldBefore = values.Contains(oldKey);
        var countBefore = values.Count;
        Assert.True(oldBefore);
        Assert.Equal(1, countBefore);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", [new TsMapValue(values), TsValue.FromBool(checkReturn)]);
        var oldAfter = values.Contains(oldKey);
        var countAfter = values.Count;
        Assert.False(oldAfter);
        Assert.Equal(0, countAfter);

        values.Set(newKey, TsValue.FromInt32(9));
        var newAfterReuse = values.Contains(newKey);
        var oldAfterReuse = values.Contains(oldKey);
        var countAfterReuse = values.Count;
        var newValue = Assert.IsType<TsInt32Value>(values.Get(newKey)).Value;
        Assert.True(newAfterReuse);
        Assert.False(oldAfterReuse);
        Assert.Equal(1, countAfterReuse);
        Assert.Equal(9, newValue);

        var actualTag = Assert.IsType<TsInt32Value>(result).Value;
        output.WriteLine($"Map.clear row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; before(old={oldBefore},count={countBefore}); after(old={oldAfter},count={countAfter}); reuse(new={newAfterReuse},old={oldAfterReuse},count={countAfterReuse},int32Value={newValue}); expectedTag={expectedTag}; actualTag={actualTag}");
        Assert.Equal(expectedTag, actualTag);
    }
}
