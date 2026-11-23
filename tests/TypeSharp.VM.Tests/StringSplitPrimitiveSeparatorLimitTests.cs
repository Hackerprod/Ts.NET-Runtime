using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringSplitPrimitiveSeparatorLimitTests(ITestOutputHelper output)
{
    private sealed record Row(string Id, string Text, TsValue Separator, TsValue? Limit, string[] Expected);

    [Fact]
    public void Split_HonorsLimitForPrimitiveNumberAndBooleanSeparators()
    {
        var withLimit = Compile("function main(text: string, separator: any, limit: any): string[] { return text.split(separator, limit); }", 3);
        var withoutLimit = Compile("function main(text: string, separator: any): string[] { return text.split(separator); }", 2);

        Row[] rows =
        [
            new("number-cap", "a0b0c", TsValue.FromInt32(0), TsValue.FromInt32(1), ["a"]),
            new("true-zero", "atruebtruec", TsValue.FromBool(true), TsValue.FromInt32(0), []),
            new("false-bool-cap", "afalsebfalsec", TsValue.FromBool(false), TsValue.FromBool(true), ["a"]),
            new("string-cap", "a0b0c", TsValue.FromString("0"), TsValue.FromInt32(1), ["a"]),
            new("number-omitted-limit", "a0b0c", TsValue.FromInt32(0), null, ["a", "b", "c"]),
            new("number-undefined-limit", "a0b0c", TsValue.FromInt32(0), TsValue.Void, ["a", "b", "c"]),
            new("bool-no-match", "abc", TsValue.FromBool(true), TsValue.FromInt32(1), ["abc"])
        ];

        var observations = rows.Select(row =>
        {
            var compiled = row.Limit is null ? withoutLimit : withLimit;
            TsValue[] args = row.Limit is { } limit
                ? [TsValue.FromString(row.Text), row.Separator, limit]
                : [TsValue.FromString(row.Text), row.Separator];
            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            var error = Record.Exception(() => { result = interpreter.Execute(compiled.Code, "main", args); });
            var array = result as TsArrayValue;
            var indexes = Enumerable.Range(0, array?.Value.Count ?? 0).ToArray();
            var holes = indexes.Select(index => array!.Value.IsHole(index)).ToArray();
            var items = indexes.Select(index => holes[index] ? null : array!.Value.Get(index)).ToArray();
            var actual = items.Select(item => (item as TsStringValue)?.Value).ToArray();
            var returnType = result?.GetType().Name;
            var dense = array is not null && !holes.Contains(true);
            var itemTypes = items.Select(item => item?.GetType().Name).ToArray();
            var escape = error?.GetType().FullName;
            output.WriteLine("C177 String.split " + JsonSerializer.Serialize(new
            {
                row = row.Id,
                compiled.ParserCount, compiled.BinderCount, compiled.Target, compiled.Argc, compiled.LoadArgs,
                inputs = args.Select(Describe).ToArray(),
                returnType, dense, itemTypes, actual, escape
            }));
            return (row, dense, items, actual, escape);
        }).ToArray();

        var mismatches = observations
            .Where(item => item.escape is not null || !item.dense || item.items.Any(value => value is not TsStringValue)
                || !item.row.Expected.SequenceEqual(item.actual, StringComparer.Ordinal))
            .Select(item => item.row.Id).ToArray();
        Assert.True(mismatches.Length == 0, "C177_STRING_SPLIT_MISMATCH_IDS: " + string.Join(", ", mismatches));
    }

    private static object Describe(TsValue input) => new
    {
        type = input.GetType().Name,
        value = input switch
        {
            TsStringValue text => (object?)text.Value,
            TsInt32Value number => number.Value,
            TsBoolValue boolean => boolean.Value,
            TsVoid => null,
            _ => throw new InvalidOperationException($"Unexpected fixture input {input.GetType().Name}")
        }
    };

    private static (BytecodeModule Code, int ParserCount, int BinderCount, string Target, int Argc, int[] LoadArgs)
        Compile(string source, int argc)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = Assert.Single(main.Blocks).Instructions.ToArray();
        Assert.Equal(argc + 2, instructions.Length);
        for (int index = 0; index < argc; index++)
        {
            Assert.Equal(Opcode.LoadArg, instructions[index].Opcode);
            Assert.Equal(index, instructions[index].Operand0);
        }
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal(Opcode.Call, instructions[argc].Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::split", target);
        Assert.Equal(argc, call.Operand1);
        Assert.Equal(Opcode.Return, instructions[argc + 1].Opcode);
        return (BytecodeCompiler.Compile(ir), parser.Diagnostics.Count(), binder.Diagnostics.All.Count(), target,
            call.Operand1, instructions.Take(argc).Select(instruction => instruction.Operand0).ToArray());
    }
}
