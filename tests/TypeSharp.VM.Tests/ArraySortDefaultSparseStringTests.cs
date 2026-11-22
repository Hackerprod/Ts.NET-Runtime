using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortDefaultSparseStringTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("sparse-middle", "string", "", "PHP", "PPH")]
    [InlineData("sparse-explicit-undefined", "any", "undefined", "PHPP", "PPPH")]
    [InlineData("all-hole", "string", "", "HHH", "HHH")]
    [InlineData("dense-string", "string", "", "PP", "PP")]
    [InlineData("empty", "string", "undefined", "", "")]
    public void ArraySort_PreservesSparseStringSlotsWithDefaultComparator(
        string row, string type, string argument, string expectedInputMask, string expectedMask)
    {
        var source = $$"""
            function main(a: {{type}}[]): {{type}}[] { return a.sort({{argument}}); }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("a", parameter.Name);
        Assert.False(parameter.IsRest);
        var parameterType = Assert.IsType<TsArrayType>(parameter.Type);
        var returnType = Assert.IsType<TsArrayType>(main.ReturnType);
        Assert.False(parameterType.IsReadonly);
        Assert.False(returnType.IsReadonly);
        Assert.Equal(type, parameterType.ElementType.Name);
        Assert.Equal(parameterType.Name, returnType.Name);

        var argc = argument.Length == 0 ? 1 : 2;
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.Equal(argc == 1
            ? new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }
            : new[] { Opcode.LoadArg, Opcode.LoadConst_Void, Opcode.Call, Opcode.Return },
            ops.Select(op => op.Opcode));
        var load = ops[0];
        var call = ops[^2];
        var ret = ops[^1];
        Assert.Equal(0, load.Operand0);
        Assert.Equal("Array::sort", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);
        Assert.Single(main.Blocks, block => block.Instructions.Contains(load)
            && block.Instructions.Contains(call) && block.Instructions.Contains(ret));

        var a = new TsStringValue("a");
        var b = new TsStringValue("b");
        TsValue?[] input = row switch
        {
            "sparse-middle" => [b, null, a],
            "sparse-explicit-undefined" => [b, null, TsValue.Void, a],
            "all-hole" => [null, null, null],
            "dense-string" => [b, a],
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        TsValue?[] expected = row switch
        {
            "sparse-middle" => [a, b, null],
            "sparse-explicit-undefined" => [a, b, TsValue.Void, null],
            "dense-string" => [a, b],
            _ => input
        };
        var backing = new TsArray();
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] is TsValue value) backing.Set(i, value);
            else backing.SetHole(i);
        }
        var receiver = new TsArrayValue(backing);
        Assert.Equal(input.Length, backing.Count);
        var inputMask = Mask(Slots());
        Assert.Equal(expectedInputMask, inputMask);
        Assert.Equal(Mask(input), inputMask);
        Assert.Equal(expectedMask, Mask(expected));
        AssertSlots(input);
        var before = Slots().Select(Describe).ToArray();

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", [receiver]); });
        var actual = Slots();
        var actualMask = Mask(actual);
        output.WriteLine("C125 Array.sort row " + JsonSerializer.Serialize(new
        {
            row, source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            entry = main.Name, parameter = parameter.Name, parameterType = parameterType.Name,
            returnType = returnType.Name, receiverLoad = load.Opcode.ToString(), receiverSlot = load.Operand0,
            comparator = argc == 1 ? "omitted" : ops[1].Opcode.ToString(),
            call = call.Opcode.ToString(), target = call.OperandObject, argc, valueReturn = ret.Opcode.ToString(),
            escaped = escaped?.GetType().FullName, returnTag = result?.ValueType.ToString(),
            sameReceiver = ReferenceEquals(receiver, result),
            sameBacking = ReferenceEquals(backing, (result as TsArrayValue)?.Value), count = backing.Count,
            inputMask, expectedMask, actualMask, before,
            expected = expected.Select(Describe), actual = actual.Select(Describe)
        }));
        Assert.Null(escaped);
        var returned = Assert.IsType<TsArrayValue>(result);
        Assert.Equal(TsValueType.Array, returned.ValueType);
        Assert.Same(receiver, returned);
        Assert.Same(backing, returned.Value);
        Assert.Equal(input.Length, backing.Count);
        Assert.True(expectedMask == actualMask,
            $"C125_ARRAY_SORT_SPARSE_HOLE_MASK_MISMATCH row={row}; expected={expectedMask}; actual={actualMask}");
        AssertSlots(expected);
        foreach (var original in input.OfType<TsValue>())
            Assert.Single(actual, value => ReferenceEquals(original, value));

        TsValue?[] Slots() => Enumerable.Range(0, backing.Count)
            .Select(i => backing.IsHole(i) ? null : backing.Get(i)).ToArray();

        static string Mask(IEnumerable<TsValue?> slots) =>
            new(slots.Select(value => value is null ? 'H' : 'P').ToArray());

        void AssertSlots(TsValue?[] slots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Assert.Equal(slots[i] is null, backing.IsHole(i));
                var actualValue = backing.Get(i);
                Assert.Same(slots[i] ?? TsValue.Void, actualValue);
                if (slots[i] is TsStringValue original)
                {
                    Assert.Equal(TsValueType.String, actualValue.ValueType);
                    Assert.Equal(ReferenceEquals(original, a) ? "a" : "b",
                        Assert.IsType<TsStringValue>(actualValue).Value);
                }
                else
                    Assert.IsType<TsVoid>(actualValue);
            }
        }

        string Describe(TsValue? value) => value is null ? "hole" :
            $"ref{Array.FindIndex(input, original => ReferenceEquals(original, value))}:{value.ValueType}:{value.RawValue}";
    }
}
