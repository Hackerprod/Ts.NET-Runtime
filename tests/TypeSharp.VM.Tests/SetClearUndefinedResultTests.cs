using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class SetClearUndefinedResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("mutation-only", "ab", false, 1)]
    [InlineData("nonempty", "ab", true, 1)]
    [InlineData("empty", "", true, 1)]
    [InlineData("repeat", "a", true, 2)]
    public void SetClear_EmptiesReceiverAndReturnsUndefined(string row, string members, bool checkReturn, int calls)
    {
        const string source = """
            function main(values: Set<string>, checkReturn: boolean): int32 {
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
        var parserCount = parser.Diagnostics.Count();
        var binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        var blocks = main.Blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var ops = blocks.SelectMany(block => block).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode == Opcode.CallVirt);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Set::clear", target);
        Assert.Equal(1, call.Operand1);
        var callBlock = Assert.Single(blocks, block => block.Any(op => op.Opcode == Opcode.Call));
        int callIndex = Array.FindIndex(callBlock, op => op.Opcode == Opcode.Call);
        Assert.True(callIndex > 0);
        var receiver = callBlock[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, receiver.Opcode);
        Assert.Equal(0, receiver.Operand0);
        var bytecode = BytecodeCompiler.Compile(ir);

        var values = new TsSet();
        var initial = members.Select(ch => ch.ToString()).ToArray();
        foreach (var member in initial)
            Assert.True(values.Add(TsValue.FromString(member)));
        var a = TsValue.FromString("a");
        var b = TsValue.FromString("b");
        var n = TsValue.FromString("new");
        var wrapper = new TsSetValue(values);
        TsValue[] args = [wrapper, TsValue.FromBool(checkReturn)];
        var vm = new TypeSharp.VM.Interpreter.Interpreter();

        (string[] entries, int count, bool hasA, bool hasB, bool hasNew) Snapshot(string[] expected)
        {
            Assert.Same(wrapper, args[0]);
            Assert.Same(values, wrapper.Value);
            var entries = values.Entries.Select(item => Assert.IsType<TsStringValue>(item).Value).ToArray();
            int count = values.Count;
            bool hasA = values.Contains(a), hasB = values.Contains(b), hasNew = values.Contains(n);
            Assert.Equal(expected, entries);
            Assert.Equal(expected.Length, count);
            Assert.Equal(expected.Contains("a"), hasA);
            Assert.Equal(expected.Contains("b"), hasB);
            Assert.Equal(expected.Contains("new"), hasNew);
            return (entries, count, hasA, hasB, hasNew);
        }

        var tags = new List<int>();
        var observations = new List<object>();
        for (int ordinal = 1; ordinal <= calls; ordinal++)
        {
            var before = Snapshot(ordinal == 1 ? initial : []);
            var result = vm.Execute(bytecode, "main", args);
            var actual = Assert.IsType<TsInt32Value>(result).Value;
            var after = Snapshot([]);
            tags.Add(actual);
            observations.Add(new
            {
                row, ordinal, completed = true, returnType = result.GetType().Name, actual,
                before = new { before.entries, before.count, before.hasA, before.hasB, before.hasNew },
                after = new { after.entries, after.count, after.hasA, after.hasB, after.hasNew },
                sameWrapper = ReferenceEquals(wrapper, args[0]), sameBacking = ReferenceEquals(values, wrapper.Value)
            });
        }

        bool added = values.Add(n);
        Assert.True(added);
        var reuse = Snapshot(["new"]);
        var expectedTags = Enumerable.Repeat(checkReturn ? 1 : 0, calls).ToArray();
        output.WriteLine("C98 Set.clear row " + JsonSerializer.Serialize(new
        {
            row, initial, checkReturn, calls, source, parserCount, binderCount, target, argc = call.Operand1,
            opcode = call.Opcode.ToString(), receiverOpcode = receiver.Opcode.ToString(),
            receiverArgument = receiver.Operand0, observations,
            tags, expectedTags, added, reuse = new { reuse.entries, reuse.count, reuse.hasA, reuse.hasB, reuse.hasNew }
        }));
        Assert.True(expectedTags.SequenceEqual(tags),
            $"C98_SET_CLEAR_RETURN_MISMATCH {row}: expected [{string.Join(",", expectedTags)}]; actual [{string.Join(",", tags)}]");
    }
}
