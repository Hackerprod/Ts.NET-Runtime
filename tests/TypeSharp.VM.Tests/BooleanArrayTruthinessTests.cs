using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class BooleanArrayTruthinessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("empty-array", true)]
    [InlineData("nonempty-zero-array", true)]
    [InlineData("array-through-any", true)]
    [InlineData("omitted", false)]
    [InlineData("undefined", false)]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("empty-string", false)]
    [InlineData("nonempty-zero-string", true)]
    [InlineData("numeric-zero", false)]
    [InlineData("numeric-nonzero", true)]
    public void Boolean_ReturnsTrueForOrdinaryArrays(string row, bool expected)
    {
        bool omitted = row == "omitted";
        bool typedArray = row is "empty-array" or "nonempty-zero-array";
        string source = omitted ? "function main(): bool { return Boolean(); }"
            : typedArray ? "function main(input: number[]): bool { return Boolean(input); }"
            : "function main(input: any): bool { return Boolean(input); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal(TsType.Bool, main.ReturnType);
        string? inputIRType = null;
        if (omitted)
            Assert.Empty(main.Parameters);
        else
        {
            var parameter = Assert.Single(main.Parameters);
            Assert.Equal("input", parameter.Name);
            Assert.False(parameter.IsRest);
            if (typedArray)
            {
                var arrayType = Assert.IsType<TsArrayType>(parameter.Type);
                Assert.Equal(TsType.Number, arrayType.ElementType);
                Assert.False(arrayType.IsReadonly);
            }
            else
                Assert.IsType<TsAnyType>(parameter.Type);
            inputIRType = parameter.Type.Name;
        }
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        string target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Boolean", target);
        Assert.Equal(omitted ? 0 : 1, call.Operand1);
        string? inputOp = null;
        int? inputArg = null;
        if (!omitted)
        {
            var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
            int callIndex = block.Instructions.IndexOf(call);
            Assert.True(callIndex >= 1);
            var load = block.Instructions[callIndex - 1];
            Assert.Equal(Opcode.LoadArg, load.Opcode);
            Assert.Equal(0, load.Operand0);
            inputOp = load.Opcode.ToString();
            inputArg = load.Operand0;
        }

        int[]? elements = row switch
        {
            "empty-array" => [],
            "nonempty-zero-array" => [0],
            "array-through-any" => [0, 1],
            _ => null
        };
        TsArrayValue? wrapper = elements is null ? null : new TsArrayValue(new TsArray());
        if (wrapper is not null)
            foreach (int element in elements!)
                wrapper.Value.Add(new TsInt32Value(element));
        TsValue[] args = omitted ? [] : [row switch
        {
            "empty-array" or "nonempty-zero-array" or "array-through-any" => wrapper!,
            "undefined" => TsValue.Void,
            "null" => TsValue.Null,
            "false" => new TsBoolValue(false),
            "true" => new TsBoolValue(true),
            "empty-string" => new TsStringValue(""),
            "nonempty-zero-string" => new TsStringValue("0"),
            "numeric-zero" => new TsInt32Value(0),
            "numeric-nonzero" => new TsInt32Value(-2),
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        }];
        TsValue? input = omitted ? null : Assert.Single(args);
        TsArray? backing = wrapper?.Value;
        var before = Snapshot(row, input, elements);

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        TsValue? afterInput = omitted ? null : Assert.Single(args);
        var after = Snapshot(row, afterInput, elements);
        bool? sameWrapper = omitted ? null : ReferenceEquals(input, afterInput);
        bool? sameBacking = wrapper is null ? null : ReferenceEquals(backing, ((TsArrayValue)afterInput!).Value);
        output.WriteLine("C100 Boolean array row " + JsonSerializer.Serialize(new
        {
            row, source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            target, opcode = call.Opcode.ToString(), argc = call.Operand1, inputIRType, inputOp, inputArg,
            before, after, sameWrapper, sameBacking, escaped = escaped?.GetType().FullName,
            actualType = result?.GetType().Name, actual = (result as TsBoolValue)?.Value,
            expectedType = nameof(TsBoolValue), expected
        }));
        Assert.Null(escaped);
        Assert.Equal(before, after);
        if (!omitted) Assert.True(sameWrapper);
        if (wrapper is not null) Assert.True(sameBacking);
        bool actual = Assert.IsType<TsBoolValue>(result).Value;
        Assert.True(expected == actual,
            $"C100_BOOLEAN_ARRAY_RESULT_MISMATCH {row}: expected {(expected ? "true" : "false")}; actual {(actual ? "true" : "false")}");
    }

    private static string Snapshot(string row, TsValue? input, int[]? elements)
    {
        int? count = null;
        object? value = null;
        switch (row)
        {
            case "empty-array":
            case "nonempty-zero-array":
            case "array-through-any":
                var array = Assert.IsType<TsArrayValue>(input).Value;
                count = array.Count;
                Assert.Equal(elements!.Length, array.Count);
                var entries = Enumerable.Range(0, array.Count).Select(i =>
                {
                    var entry = Assert.IsType<TsInt32Value>(array.Get(i));
                    Assert.Equal(elements[i], entry.Value);
                    return new { type = entry.GetType().Name, value = entry.Value };
                }).ToArray();
                value = entries;
                break;
            case "omitted":
                Assert.Null(input);
                break;
            case "undefined":
                Assert.Same(TsValue.Void, Assert.IsType<TsVoid>(input));
                break;
            case "null":
                Assert.Same(TsValue.Null, Assert.IsType<TsNull>(input));
                break;
            case "false":
            case "true":
                bool boolean = Assert.IsType<TsBoolValue>(input).Value;
                Assert.Equal(row == "true", boolean);
                value = boolean;
                break;
            case "empty-string":
            case "nonempty-zero-string":
                string str = Assert.IsType<TsStringValue>(input).Value;
                Assert.Equal(row == "empty-string" ? "" : "0", str);
                value = str;
                break;
            case "numeric-zero":
            case "numeric-nonzero":
                int i32 = Assert.IsType<TsInt32Value>(input).Value;
                Assert.Equal(row == "numeric-zero" ? 0 : -2, i32);
                value = i32;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
        return JsonSerializer.Serialize(new { type = input?.GetType().Name, count, value });
    }
}
