using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortDefaultIntegralFloat64OrderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("float64-omitted", "number", "", 2, 1, 0)]
    [InlineData("mixed-omitted", "number", "", 2, 1, 0)]
    [InlineData("mixed-undefined-negative", "number", "undefined", 2, 1, 0)]
    [InlineData("int32-omitted-control", "number", "", 2, 1, 0)]
    [InlineData("float64-comparator-control", "number", "(a: number, b: number): number => a - b", 2, 0, 1)]
    [InlineData("string-omitted-control", "string", "", 2, 1, 0)]
    public void Sort_MutatesDenseArrayWithExpectedReferenceOrder(
        string row, string type, string argument, int first, int second, int third)
    {
        var source = $$"""
            function main(Array: {{type}}[]): {{type}}[] { return Array.sort({{argument}}); }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("Array", parameter.Name);
        Assert.False(parameter.IsRest);
        var parameterType = Assert.IsType<TsArrayType>(parameter.Type);
        Assert.False(parameterType.IsReadonly);
        Assert.Equal(type, parameterType.ElementType.Name);
        Assert.Equal(parameterType.Name, Assert.IsType<TsArrayType>(main.ReturnType).Name);

        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic
            or Opcode.CallDynamicArray or Opcode.ReturnVoid);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::sort", target);
        var argc = argument.Length == 0 ? 1 : 2;
        Assert.Equal(argc, call.Operand1);
        var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
        var callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= argc && callIndex + 1 < block.Instructions.Count);
        var load = block.Instructions[callIndex - argc];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        var ret = Assert.Single(ops, op => op.Opcode == Opcode.Return);
        Assert.Same(ret, block.Instructions[callIndex + 1]);

        var comparator = "omitted";
        if (argc == 2)
        {
            var comparatorLoad = block.Instructions[callIndex - 1];
            if (argument == "undefined")
            {
                Assert.Equal(Opcode.LoadConst_Void, comparatorLoad.Opcode);
                comparator = comparatorLoad.Opcode.ToString();
            }
            else
            {
                Assert.Equal(Opcode.LoadFunc, comparatorLoad.Opcode);
                var name = Assert.IsType<string>(comparatorLoad.OperandObject);
                var function = Assert.Single(ir.Functions, function => function.Name == name);
                Assert.NotSame(main, function);
                Assert.Equal(new[] { "a", "b" }, function.Parameters.Select(p => p.Name));
                Assert.All(function.Parameters, p => { Assert.Equal("number", p.Type.Name); Assert.False(p.IsRest); });
                Assert.Equal("number", function.ReturnType.Name);
                comparator = $"{comparatorLoad.Opcode}({name}(a:number,b:number):number)";
            }
        }

        TsValue[] originals = row switch
        {
            "float64-omitted" or "float64-comparator-control" => [new TsFloat64Value(2), new TsFloat64Value(10), new TsFloat64Value(1)],
            "mixed-omitted" => [new TsInt32Value(2), new TsFloat64Value(10), new TsInt32Value(1)],
            "mixed-undefined-negative" => [new TsFloat64Value(-2), new TsInt32Value(-10), new TsFloat64Value(-1)],
            "int32-omitted-control" => [new TsInt32Value(2), new TsInt32Value(10), new TsInt32Value(1)],
            "string-omitted-control" => [new TsStringValue("2"), new TsStringValue("10"), new TsStringValue("1")],
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        var backing = new TsArray();
        foreach (var original in originals)
            backing.Add(original);
        var receiver = new TsArrayValue(backing);
        TsValue[] expected = [originals[first], originals[second], originals[third]];
        var before = originals.Select(Capture).ToArray();
        Assert.Equal(originals.Length, backing.Count);
        Assert.All(Enumerable.Range(0, backing.Count), i =>
        {
            Assert.False(backing.IsHole(i));
            Assert.Same(originals[i], backing.Get(i));
        });

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", [receiver]); });
        Assert.Null(escaped);
        var returnedArray = Assert.IsType<TsArrayValue>(result);
        Assert.Equal(TsValueType.Array, returnedArray.ValueType);
        Assert.Same(receiver, returnedArray);
        Assert.Same(backing, returnedArray.Value);
        Assert.Equal(originals.Length, backing.Count);
        var actual = Enumerable.Range(0, backing.Count).Select(backing.Get).ToArray();
        Assert.All(Enumerable.Range(0, backing.Count), i => Assert.False(backing.IsHole(i)));
        var after = originals.Select(original =>
            Assert.Single(actual, candidate => ReferenceEquals(original, candidate))).Select(Capture).ToArray();
        Assert.Equal(before, after);

        var expectedOrder = expected.Select(Describe).ToArray();
        var actualOrder = actual.Select(Describe).ToArray();
        output.WriteLine("C117 Array.sort row " + JsonSerializer.Serialize(new
        {
            row, source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            entry = main.Name, parameter = parameter.Name, parameterType = parameterType.Name,
            receiverLoad = load.Opcode.ToString(), receiverSlot = load.Operand0, comparator,
            call = call.Opcode.ToString(), target, argc, valueReturn = ret.Opcode.ToString(),
            escaped = escaped?.GetType().FullName, returnType = returnedArray.GetType().Name,
            returnTag = returnedArray.ValueType.ToString(), sameReceiver = ReferenceEquals(receiver, returnedArray),
            sameBacking = ReferenceEquals(backing, returnedArray.Value), count = backing.Count,
            allPresent = true, originalReferencesOnce = true, before, after, expectedOrder, actualOrder
        }));
        Assert.True(expected.Zip(actual).All(pair => ReferenceEquals(pair.First, pair.Second)),
            $"C117_SORT_ORDER_MISMATCH row={row}; expected=[{string.Join(",", expectedOrder)}]; actual=[{string.Join(",", actualOrder)}]");

        string Describe(TsValue value)
        {
            var slot = Array.FindIndex(originals, original => ReferenceEquals(original, value));
            return $"ref{slot}:{value.ValueType}:{value.RawValue}";
        }
    }

    private sealed record ElementSnapshot(string ClrType, string Tag, object? Raw, long? Float64Bits);

    private static ElementSnapshot Capture(TsValue value) =>
        new(value.GetType().Name, value.ValueType.ToString(), value.RawValue,
            value is TsFloat64Value floating ? BitConverter.DoubleToInt64Bits(floating.Value) : null);
}
