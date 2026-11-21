using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringSplitBooleanLimitTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Rows()
    {
        yield return ["true-comma", "0061+002C+0062+002C+0063", "002C", "true", new[] { "0061" }];
        yield return ["false-comma", "0061+002C+0062", "002C", "false", Array.Empty<string>()];
        yield return ["true-multi-separator", "0061+003A+003A+0062+003A+003A+0063", "003A+003A", "true", new[] { "0061" }];
        yield return ["false-no-match", "0061+0062+0063", "002C", "false", Array.Empty<string>()];
        yield return ["true-empty-separator-surrogates", "D83D+DE00+007A", "", "true", new[] { "D83D" }];
        yield return ["false-empty-separator", "0061+0062", "", "false", Array.Empty<string>()];
        yield return ["false-empty-input", "", "002C", "false", Array.Empty<string>()];
        yield return ["true-empty-input", "", "002C", "true", new[] { "" }];
        yield return ["true-no-match", "0061+0062+0063", "002C", "true", new[] { "0061+0062+0063" }];
        yield return ["true-both-empty", "", "", "true", Array.Empty<string>()];
        yield return ["numeric-two", "0061+002C+0062+002C+0063", "002C", "2", new[] { "0061", "0062" }];
        yield return ["numeric-nan", "0061+002C+0062", "002C", "NaN", Array.Empty<string>()];
    }

    private static string Decode(string units) => new(units.Split('+', StringSplitOptions.RemoveEmptyEntries)
        .Select(unit => (char)ushort.Parse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());

    private static string Encode(string text) =>
        string.Join("+", text.Select(unit => ((ushort)unit).ToString("X4", CultureInfo.InvariantCulture)));

    [Theory]
    [MemberData(nameof(Rows))]
    public void StringSplit_UsesSuppliedBooleanLimit(string row, string receiver, string separator, string token, string[] expected)
    {
        var source = $"function main(s: string, separator: string): string[] {{ return s.split(separator, {token}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        Assert.Equal(new[] { "s", "separator" }, main.Parameters.Select(p => p.Name));
        var instructions = Assert.Single(main.Blocks).Instructions.ToArray();
        Assert.Equal(5, instructions.Length);
        Assert.Equal(Opcode.LoadArg, instructions[0].Opcode);
        Assert.Equal(0, instructions[0].Operand0);
        Assert.Equal(Opcode.LoadArg, instructions[1].Opcode);
        Assert.Equal(1, instructions[1].Operand0);
        Assert.Equal(Opcode.Call, instructions[3].Opcode);
        string target = Assert.IsType<string>(instructions[3].OperandObject);
        int argc = instructions[3].Operand1;
        Assert.Equal("String::split", target);
        Assert.Equal(3, argc);
        Assert.Equal(Opcode.Return, instructions[4].Opcode);

        var limit = instructions[2];
        string limitValue;
        if (token is "true" or "false")
        {
            Assert.Equal(Opcode.LoadConst_Bool, limit.Opcode);
            Assert.True(limit.Operand0 is 0 or 1);
            limitValue = limit.Operand0 == 1 ? "true" : "false";
            Assert.Equal(token, limitValue);
        }
        else if (token == "2" && limit.Opcode == Opcode.LoadConst_I32)
        {
            Assert.Equal(2, limit.Operand0);
            limitValue = limit.Operand0.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            Assert.Equal(Opcode.LoadConst_F64, limit.Opcode);
            double number = Assert.IsType<double>(limit.OperandObject);
            if (token == "NaN")
                Assert.True(double.IsNaN(number));
            else
            {
                Assert.Equal("2", token);
                Assert.Equal(2d, number);
            }
            limitValue = number.ToString("R", CultureInfo.InvariantCulture);
        }

        TsValue[] args = [TsValue.FromString(Decode(receiver)), TsValue.FromString(Decode(separator))];
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var result = interpreter.Execute(BytecodeCompiler.Compile(ir), "main", args);
        var array = Assert.IsType<TsArrayValue>(result).Value;
        var actual = new string[array.Count];
        var itemTypes = new string[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            Assert.False(array.IsHole(i));
            var item = Assert.IsType<TsStringValue>(array.Get(i));
            actual[i] = Encode(item.Value);
            itemTypes[i] = item.GetType().Name;
        }

        output.WriteLine("C96 ALT split row " + JsonSerializer.Serialize(new
        {
            row, parserCount, binderCount, target, argc, limitOpcode = limit.Opcode.ToString(), limitValue,
            loadArgs = new[] { instructions[0].Operand0, instructions[1].Operand0 },
            operations = instructions.Select(i => i.Opcode.ToString()).ToArray(),
            inputs = args.Select(arg => Encode(Assert.IsType<TsStringValue>(arg).Value)).ToArray(),
            inputTypes = args.Select(arg => arg.GetType().Name).ToArray(),
            returned = true, returnType = result.GetType().Name, itemTypes, expected, actual,
            expectedCount = expected.Length, actualCount = array.Count
        }));
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"C96_ALT_SPLIT_LIMIT_MISMATCH {row}: expected {JsonSerializer.Serialize(expected)}; actual {JsonSerializer.Serialize(actual)}");
    }
}
