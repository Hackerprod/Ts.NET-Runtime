using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using ScriptParser = TypeSharp.Syntax.Parser.Parser;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class StringSplitNullLimitTests
{
    internal sealed record Row(string Id, string Text, TsValue? Separator, TsValue? Limit, string[] Expected, bool VmOnly = false);
    private static TsValue S(string text) => TsValue.FromString(text);
    private static TsValue N(int value) => TsValue.FromInt32(value);

    internal static IEnumerable<Row> Rows()
    {
        foreach (var (id, separator, all, first) in new (string, TsValue, string[], string[])[]
        {
            ("comma", S(","), ["a", "b"], ["a"]),
            ("empty", S(""), ["a", ",", "b"], ["a"]),
            ("undefined", TsValue.Void, ["a,b"], ["a,b"]),
        })
        {
            yield return new(id + "-null", "a,b", separator, TsValue.Null, []);
            yield return new(id + "-omitted", "a,b", separator, null, all);
            yield return new(id + "-undefined", "a,b", separator, TsValue.Void, all);
            yield return new(id + "-zero", "a,b", separator, N(0), []);
            yield return new(id + "-one", "a,b", separator, N(1), first);
            yield return new(id + "-text-zero", "a,b", separator, S("0"), []);
            yield return new(id + "-text-one", "a,b", separator, S("1"), first);
            yield return new(id + "-text-null", "a,b", separator, S("null"), all, VmOnly: true);
        }
        foreach (var (id, receiver, separator) in new (string, string, TsValue)[]
        {
            ("int", "a0b", N(0)),
            ("bool", "afalseb", TsValue.FromBool(false)),
        })
        {
            yield return new(id + "-null", receiver, separator, TsValue.Null, []);
            yield return new(id + "-zero", receiver, separator, N(0), []);
            yield return new(id + "-one", receiver, separator, N(1), ["a"]);
            yield return new(id + "-text-one", receiver, separator, S("1"), ["a", "b"], VmOnly: true);
        }
        yield return new("empty-receiver-comma-null", "", S(","), TsValue.Null, []);
        yield return new("empty-receiver-undefined-null", "", TsValue.Void, TsValue.Null, []);
        yield return new("empty-receiver-empty-null", "", S(""), TsValue.Null, []);
        yield return new("no-separator", "a,b", null, null, ["a,b"]);
        yield return new("null-separator", "anullb", TsValue.Null, TsValue.Null, ["a", "b"], VmOnly: true);
        yield return new("regex-separator", "a/,/b", TsValue.FromRegex(",", ""), TsValue.Null, ["a", "b"], VmOnly: true);
    }

    internal static (string Evidence, string[] Items) Observe(Row row)
    {
        var inputs = new List<TsValue> { S(row.Text) };
        if (row.Separator is TsValue separator) inputs.Add(separator);
        if (row.Limit is TsValue limit) inputs.Add(limit);
        int argc = inputs.Count;
        string parameters = argc switch { 1 => "text: string", 2 => "text: string, separator: any", _ => "text: string, separator: any, limit: any" };
        string splitInputs = argc switch { 1 => "", 2 => "separator", _ => "separator, limit" };
        var tokens = new Lexer($"function main({parameters}): string[] {{ return text.split({splitInputs}); }}").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new ScriptParser(tokens);
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = Assert.Single(main.Blocks).Instructions;
        Assert.Equal(argc + 2, instructions.Count);
        for (int i = 0; i < argc; i++)
        {
            Assert.Equal(Opcode.LoadArg, instructions[i].Opcode);
            Assert.Equal(i, instructions[i].Operand0);
        }
        Assert.Equal(Opcode.Call, instructions[argc].Opcode);
        Assert.Equal("String::split", Assert.IsType<string>(instructions[argc].OperandObject));
        Assert.Equal(argc, instructions[argc].Operand1);
        Assert.Equal(Opcode.Return, instructions[argc + 1].Opcode);
        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        var vm = new Vm();
        var arguments = inputs.ToArray();
        var first = Assert.IsType<TsArrayValue>(vm.Execute(bytecode, "main", arguments));
        var second = Assert.IsType<TsArrayValue>(vm.Execute(bytecode, "main", arguments));
        var items = Elements(first);
        Assert.True(items.SequenceEqual(Elements(second), StringComparer.Ordinal));
        string shape = "nonempty";
        if (items.Length == 0)
        {
            Assert.Equal(0, first.Value.Capacity);
            Assert.Equal(0, second.Value.Capacity);
            Assert.NotSame(first, second);
            Assert.NotSame(first.Value, second.Value);
            shape = "zero-capacity-distinct";
        }
        return ($"lexer=0 parser=0 binder=0 verified call=String::split/{argc} inputs={string.Join(",", inputs.Select(value => value.GetType().Name))} shape={shape}", items);
    }

    private static string[] Elements(TsArrayValue wrapper)
    {
        Assert.Equal(TsValueType.Array, wrapper.ValueType);
        var array = wrapper.Value;
        var result = new string[array.Count];
        for (int i = 0; i < result.Length; i++)
        {
            Assert.False(array.IsHole(i));
            var value = Assert.IsType<TsStringValue>(array.Get(i));
            Assert.Equal(TsValueType.String, value.ValueType);
            result[i] = value.Value;
        }
        return result;
    }

    [Fact]
    public void Split_ExplicitNullLimitsUseFreshZeroLengthArraysOnSupportedSeparators()
    {
        var failures = new List<string>();
        foreach (var row in Rows())
        {
            var (_, actual) = Observe(row);
            if (!row.Expected.SequenceEqual(actual, StringComparer.Ordinal))
                failures.Add($"{row.Id} expected={JsonSerializer.Serialize(row.Expected)} actual={JsonSerializer.Serialize(actual)}");
        }
        Assert.True(failures.Count == 0, "C311_SPLIT " + string.Join("; ", failures));
    }
}
