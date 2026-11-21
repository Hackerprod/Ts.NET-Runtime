using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ParseIntOmittedRadixTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("decimal-suffix", "15px", "string", 15d)]
    [InlineData("hex-lower", "0x10", "string", 16d)]
    [InlineData("signed-hex-ecma-trim", "\uFEFF\u2028-0XFtail", "string", -15d)]
    [InlineData("binary-not-auto", "0b11", "string", 0d)]
    [InlineData("octal-not-auto", "0o77", "string", 0d)]
    [InlineData("decimal-leading-zero", "077", "string", 77d)]
    [InlineData("decimal", "42", "string", 42d)]
    [InlineData("negative-zero", "-0", "string", -0.0)]
    [InlineData("no-digit", "x42", "string", double.NaN)]
    [InlineData("bare-hex-prefix", "0x", "string", double.NaN)]
    [InlineData("nel-not-trimmed", "\u008542", "string", double.NaN)]
    [InlineData("plus-suffix", " +42suffix", "string", 42d)]
    [InlineData("legacy-non-string-fraction", "42.25", "f64", double.NaN)]
    [InlineData("legacy-non-string-integer", "42", "i32", 42d)]
    public void ParseInt_OmittedRadixParsesPrimitiveStringPrefixes(string row, string input, string kind, double expected)
    {
        string source = kind == "string"
            ? "function main(input: string): number { return parseInt(input); }"
            : "function main(input: any): number { return parseInt(input); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        string target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("parseInt", target);
        Assert.Equal(1, call.Operand1);
        var block = Assert.Single(main.Blocks, candidate => candidate.Instructions.Contains(call));
        int callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= 1);
        var load = block.Instructions[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);

        var culture = CultureInfo.InvariantCulture;
        string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", culture);
        TsValue[] args = [kind switch
        {
            "string" => TsValue.FromString(input),
            "f64" => TsValue.FromFloat64(42.25),
            "i32" => TsValue.FromInt32(42),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }];
        string inputValue;
        string? inputBits = null;
        switch (kind)
        {
            case "string":
                inputValue = Assert.IsType<TsStringValue>(args[0]).Value;
                break;
            case "f64":
                var number = Assert.IsType<TsFloat64Value>(args[0]).Value;
                inputValue = number.ToString("R", culture);
                inputBits = Bits(number);
                Assert.Equal("4045200000000000", inputBits);
                break;
            default:
                inputValue = Assert.IsType<TsInt32Value>(args[0]).Value.ToString(culture);
                break;
        }
        Assert.Equal(input, inputValue);
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var actual = (result as TsFloat64Value)?.Value;
        output.WriteLine("C99 parseInt omitted " + JsonSerializer.Serialize(new
        {
            row, source, parserCount, binderCount, target, opcode = call.Opcode.ToString(), argc = call.Operand1,
            inputOp = load.Opcode.ToString(), inputArg = load.Operand0, inputType = args[0].GetType().Name,
            inputValue, inputBits, escaped = escaped?.GetType().FullName,
            actualType = result?.GetType().Name, actual = actual?.ToString("R", culture),
            actualBits = actual is double observed ? Bits(observed) : null,
            expectedType = nameof(TsFloat64Value), expected = expected.ToString("R", culture), expectedBits = Bits(expected)
        }));
        Assert.Null(escaped);
        double numeric = Assert.IsType<TsFloat64Value>(result).Value;
        Assert.True(double.IsNaN(expected) ? double.IsNaN(numeric) : Bits(numeric) == Bits(expected),
            $"C99_PARSEINT_OMITTED_RADIX_RESULT_MISMATCH {row}: expected {expected.ToString("R", culture)} [{Bits(expected)}]; actual {numeric.ToString("R", culture)} [{Bits(numeric)}]");
    }
}
