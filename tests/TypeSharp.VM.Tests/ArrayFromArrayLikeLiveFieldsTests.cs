using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFromArrayLikeLiveFieldsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("replace-later-field", "source[1] = 9;", 2, 4, 2, 9)]
    [InlineData("replace-later-with-undefined", "source[1] = undefined;", 2, 4, 2, null)]
    [InlineData("snapshot-length", "source[\"length\"] = 1;", 2, 4, 1, 4)]
    [InlineData("present-undefined", "", 2, null, 2, null)]
    [InlineData("omit-mapper", "", 1, 4, 2, 4)]
    [InlineData("explicit-undefined-mapper", "", 2, 4, 2, 4)]
    public void ArrayFrom_ReadsPresentFieldsBeforeMapping(string row, string update, int argc,
        int? initialSecond, int expectedLength, int? expectedSecond)
    {
        bool mapped = row is not ("omit-mapper" or "explicit-undefined-mapper");
        string mutation = update.Length == 0 ? "" : $"if (i === 0) {{ {update} }}";
        string suffix = row switch
        {
            "omit-mapper" => "",
            "explicit-undefined-mapper" => ", undefined",
            _ => $", (value: any, i: int32): any => {{ visits.push(i); {mutation} return value; }}",
        };
        var source = $"function main(source: any, visits: int32[]): any[] {{ return Array.from(source{suffix}); }}";
        void Log(string detail) => output.WriteLine($"C119 row={row}; {detail}");

        Log($"source={source}");
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Log($"parser={parser.Diagnostics.Count()}; binder={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var mainOps = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(mainOps, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::from", target);
        Assert.Equal(argc, call.Operand1);
        Assert.DoesNotContain(mainOps, instruction => instruction.Opcode is
            Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var closures = mainOps.Where(instruction => instruction.Opcode == Opcode.MakeClosure).ToArray();
        var callbacks = ir.Functions.Where(function => function.Name != "main").ToArray();
        var voidLoads = mainOps.Count(instruction => instruction.Opcode == Opcode.LoadConst_Void);
        Assert.Equal(row == "explicit-undefined-mapper" ? 1 : 0, voidLoads);
        if (row == "explicit-undefined-mapper")
            Assert.Equal(Opcode.LoadConst_Void, mainOps[Array.IndexOf(mainOps, call) - 1].Opcode);
        Log($"target={target}; argc={call.Operand1}; void-loads={voidLoads}; closures={closures.Length}; callbacks={callbacks.Length}");
        if (mapped)
        {
            var closure = Assert.Single(closures);
            var callback = Assert.Single(callbacks);
            Assert.Equal(callback.Name, Assert.IsType<string>(closure.OperandObject));
            Assert.Equal(update.Length == 0 ? 1 : 2, closure.Operand1);
            var callbackOps = callback.Blocks.SelectMany(block => block.Instructions).ToArray();
            var push = Assert.Single(callbackOps, instruction => instruction.Opcode == Opcode.Call);
            Assert.Equal("Array::push", Assert.IsType<string>(push.OperandObject));
            Assert.Equal(2, push.Operand1);
            var stores = callbackOps.Count(instruction => instruction.Opcode == Opcode.StoreElement);
            Assert.Equal(update.Length == 0 ? 0 : 1, stores);
            Log($"closure={closure.OperandObject}; captures={closure.Operand1}; callback={callback.Name}; push={push.OperandObject}/{push.Operand1}; stores={stores}");
        }
        else
        {
            Assert.Empty(closures);
            Assert.Empty(callbacks);
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var obj = new TsObject("Object");
        obj.SetField("length", TsValue.FromInt32(2));
        obj.SetField("0", TsValue.FromInt32(3));
        obj.SetField("1", Value(initialSecond));
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() =>
        {
            result = vm.Execute(bytecode, "main", [new TsObjectValue(obj), new TsArrayValue(visits)]);
        });
        Log($"exception={error?.GetType().Name ?? "none"}; result-type={result?.GetType().Name}; tag={result?.ValueType}");
        Assert.Null(error);

        foreach (var (key, expected) in new[] { ("length", (int?)expectedLength), ("0", 3), ("1", expectedSecond) })
        {
            bool present = obj.Fields.TryGetValue(key, out var field);
            Log($"field={key}; present={present}; expected={Display(Value(expected))}; actual={Display(field)}");
            Assert.True(present);
            Assert.True(Matches(expected, field));
        }

        var visitValues = Enumerable.Range(0, visits.Count).Select(visits.Get).ToArray();
        Log($"visits=[{string.Join(",", visitValues.Select(Display))}]");
        Assert.Equal(mapped ? new[] { 0, 1 } : Array.Empty<int>(),
            visitValues.Select(value => Assert.IsType<TsInt32Value>(value).Value).ToArray());

        var array = Assert.IsType<TsArrayValue>(result).Value;
        Log($"count={array.Count}; present0={!array.IsHole(0)}; present1={!array.IsHole(1)}; fresh={!ReferenceEquals(visits, array)}");
        Assert.NotSame(visits, array);
        Assert.Equal(2, array.Count);
        Assert.False(array.IsHole(0));
        Assert.False(array.IsHole(1));
        Log($"slot0={Display(array.Get(0))}");
        Assert.True(Matches(3, array.Get(0)));
        var second = array.Get(1);
        Log($"expected={Display(Value(expectedSecond))}; actual={Display(second)}");
        Assert.True(Matches(expectedSecond, second),
            $"C119_ARRAY_FROM_RESULT_MISMATCH row={row}; expected={Display(Value(expectedSecond))}; actual={Display(second)}");
    }

    private static TsValue Value(int? expected) =>
        expected is int value ? TsValue.FromInt32(value) : TsValue.Void;

    private static bool Matches(int? expected, TsValue? actual) =>
        expected is int value
            ? actual is TsInt32Value intValue && intValue.ValueType == TsValueType.Int32 && intValue.Value == value
            : actual is TsVoid && actual.ValueType == TsValueType.Void;

    private static string Display(TsValue? value) =>
        value is null ? "missing" : $"{value.GetType().Name}/{value.ValueType}:{value.RawValue ?? "undefined"}";
}
