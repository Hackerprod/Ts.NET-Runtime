using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class SetForEachUndefinedResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("traversal-control", false, 0)]
    [InlineData("strict-undefined", true, 1)]
    public void SetForEach_VisitsEntriesAndReturnsUndefined(string row, bool checkReturn, int expectedTag)
    {
        const string source = """
            function main(values: Set<int32>, visits: any[], checkReturn: boolean): int32 {
                let result: any = values.forEach((value: any, repeated: any, receiver: any) => {
                    visits.push(value);
                    visits.push(repeated);
                    visits.push(receiver);
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
        Assert.Equal("Set::forEach", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var values = new TsSet();
        values.Add(TsValue.FromInt32(2));
        values.Add(TsValue.FromInt32(4));
        var receiver = new TsSetValue(values);
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", [receiver, new TsArrayValue(visits), TsValue.FromBool(checkReturn)]);
        Assert.Equal(6, visits.Count);
        var firstValue = Assert.IsType<TsInt32Value>(visits.Get(0)).Value;
        var firstRepeated = Assert.IsType<TsInt32Value>(visits.Get(1)).Value;
        var firstReceiver = visits.Get(2);
        var secondValue = Assert.IsType<TsInt32Value>(visits.Get(3)).Value;
        var secondRepeated = Assert.IsType<TsInt32Value>(visits.Get(4)).Value;
        var secondReceiver = visits.Get(5);
        var actualTag = Assert.IsType<TsInt32Value>(result).Value;
        output.WriteLine($"Set.forEach row={row}; parser/binder=0/0; target=Set::forEach; argc=2; expectedVisits=[int32:2,int32:2,same:True,int32:4,int32:4,same:True]; actualVisits=[int32:{firstValue},int32:{firstRepeated},same:{ReferenceEquals(receiver, firstReceiver)},int32:{secondValue},int32:{secondRepeated},same:{ReferenceEquals(receiver, secondReceiver)}]; expectedTag={expectedTag}; actualTag={actualTag}");
        Assert.Equal((2, 2, 4, 4), (firstValue, firstRepeated, secondValue, secondRepeated));
        Assert.Same(receiver, firstReceiver);
        Assert.Same(receiver, secondReceiver);
        Assert.Equal(expectedTag, actualTag);
    }
}
