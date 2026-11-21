using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class BooleanCollectionTruthinessTests(ITestOutputHelper output)
{
    private const string Source = "function main(input: any): boolean { return Boolean(input); }";

    [Theory]
    [InlineData("empty-map", true)]
    [InlineData("populated-map", true)]
    [InlineData("empty-set", true)]
    [InlineData("populated-set", true)]
    [InlineData("empty-array-control", true)]
    [InlineData("zero-control", false)]
    public void Boolean_UsesNativeCollectionTruthiness(string row, bool expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal(TsType.Bool, main.ReturnType);
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("input", parameter.Name);
        Assert.False(parameter.IsRest);
        Assert.IsType<TsAnyType>(parameter.Type);

        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray or Opcode.ReturnVoid);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        string target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Boolean", target);
        int argc = call.Operand1;
        Assert.Equal(1, argc);
        var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
        int callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= 1 && callIndex + 1 < block.Instructions.Count);
        var load = block.Instructions[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        var ret = Assert.Single(ops, op => op.Opcode == Opcode.Return);
        Assert.Same(ret, block.Instructions[callIndex + 1]);
        string[] route = [load.Opcode.ToString(), call.Opcode.ToString(), ret.Opcode.ToString()];
        int inputArg = load.Operand0;

        var key = new TsStringValue("count");
        var zero = new TsInt32Value(0);
        TsValue input = row switch
        {
            "empty-map" or "populated-map" => new TsMapValue(new TsMap()),
            "empty-set" or "populated-set" => new TsSetValue(new TsSet()),
            "empty-array-control" => new TsArrayValue(new TsArray()),
            "zero-control" => zero,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        if (row == "populated-map") Assert.IsType<TsMapValue>(input).Value.Set(key, zero);
        if (row == "populated-set") Assert.IsType<TsSetValue>(input).Value.Add(zero);

        TsValue[] args = [input];
        object? backing = Backing(input);
        var beforeInput = Assert.Single(args);
        Assert.Same(input, beforeInput);
        Assert.Same(backing, Backing(beforeInput));
        var before = Snapshot(row, beforeInput, key, zero);

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var afterInput = Assert.Single(args);
        var after = Snapshot(row, afterInput, key, zero);
        bool sameWrapper = ReferenceEquals(input, afterInput);
        bool sameBacking = ReferenceEquals(backing, Backing(afterInput));
        output.WriteLine("C116 Boolean collection row " + JsonSerializer.Serialize(new
        {
            row, source = Source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            route, target, argc, inputArg, entry = main.Name, inputIRType = parameter.Type.Name,
            inputType = input.GetType().Name, inputTag = input.ValueType.ToString(), before, after,
            sameWrapper, sameBacking, backingType = backing?.GetType().Name,
            escaped = escaped?.GetType().FullName, actualType = result?.GetType().Name,
            actualTag = result?.ValueType.ToString(), actual = (result as TsBoolValue)?.Value,
            expectedType = nameof(TsBoolValue), expected
        }));
        Assert.Null(escaped);
        Assert.True(sameWrapper);
        Assert.True(sameBacking);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        var actual = Assert.IsType<TsBoolValue>(result);
        Assert.Equal(TsValueType.Bool, actual.ValueType);
        Assert.True(expected == actual.Value,
            $"C116_BOOLEAN_COLLECTION_RESULT_MISMATCH {row}: expected TsBoolValue {JsonSerializer.Serialize(expected)}; actual TsBoolValue {JsonSerializer.Serialize(actual.Value)}");
    }

    private static object? Backing(TsValue value) => value switch
    {
        TsMapValue map => map.Value,
        TsSetValue set => set.Value,
        TsArrayValue array => array.Value,
        _ => null
    };

    private static object Snapshot(string row, TsValue input, TsStringValue key, TsInt32Value zero)
    {
        switch (row)
        {
            case "empty-map":
            case "populated-map":
                Assert.Equal(TsValueType.Map, input.ValueType);
                var map = Assert.IsType<TsMapValue>(input).Value;
                Assert.Equal(row == "populated-map" ? 1 : 0, map.Count);
                if (map.Count == 0) return new { map.Count };
                var pair = map.GetEntryAt(0);
                var rawKey = Assert.IsType<TsStringValue>(pair.Key);
                Assert.Equal(TsValueType.String, rawKey.ValueType);
                Assert.Equal("count", rawKey.Value);
                Assert.Same(key, rawKey);
                return new
                {
                    map.Count, key = new { type = rawKey.GetType().Name, tag = rawKey.ValueType.ToString(),
                        value = rawKey.Value, sameReference = ReferenceEquals(key, rawKey) },
                    entry = VerifyZero(pair.Value, zero)
                };
            case "empty-set":
            case "populated-set":
                Assert.Equal(TsValueType.Set, input.ValueType);
                var set = Assert.IsType<TsSetValue>(input).Value;
                Assert.Equal(row == "populated-set" ? 1 : 0, set.Count);
                if (set.Count == 0) return new { set.Count };
                return new { set.Count, entry = VerifyZero(set.GetAt(0), zero) };
            case "empty-array-control":
                Assert.Equal(TsValueType.Array, input.ValueType);
                var array = Assert.IsType<TsArrayValue>(input).Value;
                Assert.Equal(0, array.Count);
                return new { array.Count };
            case "zero-control":
                return VerifyZero(input, zero);
            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

    private static object VerifyZero(TsValue value, TsInt32Value expected)
    {
        var raw = Assert.IsType<TsInt32Value>(value);
        Assert.Equal(TsValueType.Int32, raw.ValueType);
        Assert.Equal(0, raw.Value);
        Assert.Same(expected, raw);
        return new { type = raw.GetType().Name, tag = raw.ValueType.ToString(),
            value = raw.Value, sameReference = ReferenceEquals(expected, raw) };
    }
}
