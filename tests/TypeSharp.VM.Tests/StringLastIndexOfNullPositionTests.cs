using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringLastIndexOfNullPositionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("null-later-only", "ba", "null", -1)]
    [InlineData("null-zero-beats-later", "ab", "null", 0)]
    [InlineData("null-empty-needle", "", "null", 0)]
    [InlineData("numeric-zero", "ba", "0", -1)]
    [InlineData("explicit-undefined", "ba", "undefined", 3)]
    [InlineData("omitted-position", "ba", "omitted", 3)]
    [InlineData("explicit-nan", "ba", "NaN", 3)]
    [InlineData("numeric-two", "ba", "2", 1)]
    [InlineData("numeric-negative", "ab", "-1", 0)]
    [InlineData("null-no-match", "q", "null", -1)]
    public void StringLastIndexOf_UsesZeroForExplicitNullPosition(string row, string needle, string position, int expected)
    {
        const string receiver = "ababa";
        var source = position switch
        {
            "null" => "function main(s: string, needle: string): int32 { return s.lastIndexOf(needle, null); }",
            "omitted" => "function main(s: string, needle: string): int32 { return s.lastIndexOf(needle); }",
            _ => "function main(s: string, needle: string, p: number | undefined): int32 { return s.lastIndexOf(needle, p); }"
        };
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        bool hostPosition = position is not ("null" or "omitted");
        Assert.Equal(hostPosition ? new[] { "s", "needle", "p" } : new[] { "s", "needle" },
            main.Parameters.Select(p => p.Name));
        var ops = Assert.Single(main.Blocks).Instructions.ToArray();
        int argc = position == "omitted" ? 2 : 3;
        Assert.Equal(argc + 2, ops.Length);
        Assert.Equal(Opcode.LoadArg, ops[0].Opcode);
        Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(Opcode.LoadArg, ops[1].Opcode);
        Assert.Equal(1, ops[1].Operand0);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        Assert.Equal(Opcode.Call, ops[argc].Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::lastIndexOf", target);
        Assert.Equal(argc, call.Operand1);
        Assert.Equal(Opcode.Return, ops[argc + 1].Opcode);
        if (position == "null")
            Assert.Equal(Opcode.LoadConst_Null, ops[2].Opcode);
        else if (hostPosition)
        {
            Assert.Equal(Opcode.LoadArg, ops[2].Opcode);
            Assert.Equal(2, ops[2].Operand0);
        }

        var args = new List<TsValue> { TsValue.FromString(receiver), TsValue.FromString(needle) };
        string positionValue = position;
        if (position == "undefined")
        {
            args.Add(TsValue.Void);
            Assert.IsType<TsVoid>(args[2]);
        }
        else if (hostPosition)
        {
            var number = position == "NaN" ? double.NaN : double.Parse(position, CultureInfo.InvariantCulture);
            args.Add(TsValue.FromFloat64(number));
            var actualPosition = Assert.IsType<TsFloat64Value>(args[2]).Value;
            positionValue = actualPosition.ToString("R", CultureInfo.InvariantCulture);
            Assert.Equal(position, positionValue);
            if (position == "0") Assert.Equal(0L, BitConverter.DoubleToInt64Bits(actualPosition));
        }
        Assert.Equal(receiver, Assert.IsType<TsStringValue>(args[0]).Value);
        Assert.Equal(needle, Assert.IsType<TsStringValue>(args[1]).Value);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue result;
        try
        {
            result = interpreter.Execute(BytecodeCompiler.Compile(ir), "main", args.ToArray());
        }
        catch (Exception ex)
        {
            output.WriteLine($"C97 LAST_INDEX_OF EXCEPTION {row} {ex.GetType().FullName}");
            throw;
        }
        int actual = Assert.IsType<TsInt32Value>(result).Value;
        output.WriteLine("C97 lastIndexOf row " + JsonSerializer.Serialize(new
        {
            row, receiver, needle, position, positionValue, source, parserCount, binderCount, target, argc = call.Operand1,
            ops = ops.Select(op => op.Opcode.ToString()),
            loadArgs = ops.Where(op => op.Opcode == Opcode.LoadArg).Select(op => op.Operand0),
            hostTypes = args.Select(arg => arg.GetType().Name),
            abiPositionType = position == "null" ? nameof(TsNull) : hostPosition ? args[2].GetType().Name : "omitted",
            returned = true, returnType = result.GetType().Name, actual, expected
        }));
        Assert.True(expected == actual, $"C97_LAST_INDEX_OF_NULL_POSITION_MISMATCH {row}: expected {expected}; actual {actual}");
    }
}
