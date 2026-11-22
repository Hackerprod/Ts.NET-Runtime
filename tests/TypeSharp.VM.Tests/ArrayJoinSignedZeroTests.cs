using System.Reflection;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayJoinSignedZeroTests(ITestOutputHelper output)
{
    private const string Source = "function main(a: any[], sep: any): string { return a.join(sep); }";

    [Theory]
    [InlineData("f32-negative-zero", "7|0|x")]
    [InlineData("f64-negative-zero", "7|0|x")]
    [InlineData("f32-positive-zero", "7|0|x")]
    [InlineData("f64-positive-zero", "7|0|x")]
    [InlineData("f32-negative-finite", "7|-3|x")]
    [InlineData("f64-negative-finite", "7|-3|x")]
    [InlineData("mixed-undefined-separator", "7,true,,,,-0,x")]
    public void ArrayJoin_FormatsDirectSignedZeroFields(string row, string expected)
    {
        bool mixed = row == "mixed-undefined-separator";
        TsValue[] slots = mixed
            ? [new TsInt32Value(7), TsValue.FromBool(true), TsValue.Null, TsValue.Void, TsValue.Void,
                new TsStringValue("-0"), new TsStringValue("x")]
            : [new TsInt32Value(7), row switch
                {
                    "f32-negative-zero" => new TsFloat32Value(BitConverter.Int32BitsToSingle(int.MinValue)),
                    "f64-negative-zero" => new TsFloat64Value(BitConverter.Int64BitsToDouble(long.MinValue)),
                    "f32-positive-zero" => new TsFloat32Value(BitConverter.Int32BitsToSingle(0)),
                    "f64-positive-zero" => new TsFloat64Value(BitConverter.Int64BitsToDouble(0)),
                    "f32-negative-finite" => new TsFloat32Value(-3f),
                    "f64-negative-finite" => new TsFloat64Value(-3d),
                    _ => throw new ArgumentOutOfRangeException(nameof(row))
                }, new TsStringValue("x")];
        var backing = new TsArray();
        for (int i = 0; i < slots.Length; i++)
        {
            if (mixed && i == 4) backing.SetHole(i);
            else backing.Add(slots[i]);
        }

        var receiver = new TsArrayValue(backing);
        TsValue separator = mixed ? TsValue.Void : new TsStringValue("|");
        TsValue[] args = [receiver, separator];
        // Read only: a replacement buffer with the same capacity is not the same backing.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var elementsField = typeof(TsArray).GetField("_elements", fields)!;
        var presenceField = typeof(TsArray).GetField("_presence", fields)!;
        var elements = Assert.IsType<TsValue[]>(elementsField.GetValue(backing));
        var originalElements = (TsValue[])elements.Clone();
        var presence = (bool[]?)presenceField.GetValue(backing);
        var originalPresence = (bool[]?)presence?.Clone();

        object Snapshot()
        {
            Assert.Same(receiver, Assert.IsType<TsArrayValue>(args[0]));
            Assert.Equal(TsValueType.Array, args[0].ValueType);
            Assert.Same(backing, receiver.Value);
            Assert.Same(backing, receiver.RawValue);
            Assert.Same(elements, elementsField.GetValue(backing));
            Assert.Same(presence, presenceField.GetValue(backing));
            if (mixed)
            {
                Assert.NotNull(presence);
                Assert.Equal(originalPresence, presence);
            }
            else Assert.Null(presence);
            for (int i = 0; i < elements.Length; i++)
                Assert.Same(originalElements[i], elements[i]);
            Assert.Same(separator, args[1]);
            string? sep;
            if (mixed)
            {
                Assert.Same(TsValue.Void, Assert.IsType<TsVoid>(args[1]));
                sep = null;
            }
            else
            {
                sep = Assert.IsType<TsStringValue>(args[1]).Value;
                Assert.Equal("|", sep);
            }

            Assert.Equal(slots.Length, backing.Count);
            var records = new List<object>();
            for (int i = 0; i < slots.Length; i++)
            {
                bool hole = backing.IsHole(i);
                Assert.Equal(mixed && i == 4, hole);
                var slot = backing.Get(i);
                Assert.Same(slots[i], slot);
                Assert.Same(slot, elements[i]);
                object? value = (mixed, i) switch
                {
                    (_, 0) => Assert.IsType<TsInt32Value>(slot).Value,
                    (true, 1) => Assert.IsType<TsBoolValue>(slot).Value,
                    (true, 2) => Assert.IsType<TsNull>(slot).RawValue,
                    (true, 3 or 4) => Assert.IsType<TsVoid>(slot).RawValue,
                    (true, 5 or 6) or (false, 2) => Assert.IsType<TsStringValue>(slot).Value,
                    (false, 1) when row.StartsWith("f32-", StringComparison.Ordinal)
                        => BitConverter.SingleToInt32Bits(Assert.IsType<TsFloat32Value>(slot).Value).ToString("x8"),
                    (false, 1) => BitConverter.DoubleToInt64Bits(Assert.IsType<TsFloat64Value>(slot).Value).ToString("x16"),
                    _ => throw new InvalidOperationException()
                };
                object? expectedValue = (mixed, i) switch
                {
                    (_, 0) => 7,
                    (true, 1) => true,
                    (true, 2 or 3 or 4) => null,
                    (true, 5) => "-0",
                    (true, 6) or (false, 2) => "x",
                    (false, 1) => row switch
                    {
                        "f32-negative-zero" => "80000000",
                        "f64-negative-zero" => "8000000000000000",
                        "f32-positive-zero" => "00000000",
                        "f64-positive-zero" => "0000000000000000",
                        "f32-negative-finite" => "c0400000",
                        "f64-negative-finite" => "c008000000000000",
                        _ => throw new InvalidOperationException()
                    },
                    _ => throw new InvalidOperationException()
                };
                Assert.Equal(expectedValue, value);
                records.Add(new { index = i, hole, type = slot.GetType().Name, tag = slot.ValueType.ToString(), value, sameReference = true });
            }
            return new
            {
                receiver = args[0].GetType().Name, separator = args[1].GetType().Name, separatorValue = sep,
                count = backing.Count, capacity = backing.Capacity, sameArgs = true, sameBacking = true,
                sameElements = true, samePresence = true, presence, slots = records
            };
        }

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var syntax = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal(new[] { "a", "sep" }, main.Parameters.Select(p => p.Name).ToArray());
        var ops = Assert.Single(main.Blocks).Instructions.ToArray();
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.LoadArg, Opcode.Call, Opcode.Return }, ops.Select(op => op.Opcode).ToArray());
        Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(1, ops[1].Operand0);
        Assert.Equal("Array::join", Assert.IsType<string>(ops[2].OperandObject));
        Assert.Equal(2, ops[2].Operand1);
        var code = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();

        var before = JsonSerializer.SerializeToElement(Snapshot());
        TsValue? result = null;
        var exception = Record.Exception(() => { result = vm.Execute(code, "main", args); });
        Assert.Null(exception);
        var after = JsonSerializer.SerializeToElement(Snapshot());
        Assert.Equal(before.GetRawText(), after.GetRawText());
        var actual = Assert.IsType<TsStringValue>(result).Value;
        Assert.Equal(TsValueType.String, result!.ValueType);
        output.WriteLine("C124 Array.join " + JsonSerializer.Serialize(new
        {
            row, source = Source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            ops = ops.Select(op => op.Opcode.ToString()), argSlots = new[] { ops[0].Operand0, ops[1].Operand0 },
            target = ops[2].OperandObject, argc = ops[2].Operand1, before, after,
            exception = exception?.GetType().FullName, actualType = result.GetType().Name, actual, expected
        }));
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"C124_ARRAY_JOIN_SIGNED_ZERO_RESULT_MISMATCH {row}: expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual)}");
    }
}
