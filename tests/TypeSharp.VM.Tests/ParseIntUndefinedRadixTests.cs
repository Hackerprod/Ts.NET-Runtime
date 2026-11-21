using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ParseIntUndefinedRadixTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("decimal-prefix", "  -48.5", ", undefined", 2, -48d)]
    [InlineData("signed-hex", "+0x2A", ", undefined", 2, 42d)]
    [InlineData("negative-zero", "-0", ", undefined", 2, -0.0)]
    [InlineData("plain-decimal-control", "42", ", undefined", 2, 42d)]
    [InlineData("omitted-control", "  -48.5", "", 1, -48d)]
    [InlineData("explicit-hex-control", "0x2A", ", 16", 2, 42d)]
    public void ParseInt_UndefinedRadixUsesDefaultPrefix(
        string row, string input, string suffix, int arity, double expected)
    {
        var source = $"function main(input: string): number {{ return parseInt(input{suffix}); }}";
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
        Assert.Equal(arity, call.Operand1);
        var block = Assert.Single(main.Blocks, candidate => candidate.Instructions.Contains(call));
        int callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= arity && callIndex + 1 < block.Instructions.Count);
        var load = block.Instructions[callIndex - arity];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        var radixLoad = arity == 2 ? block.Instructions[callIndex - 1] : null;
        int? radixLiteral = null;
        if (suffix == ", undefined")
            Assert.Equal(Opcode.LoadConst_Void, radixLoad?.Opcode);
        if (suffix == ", 16")
        {
            Assert.NotNull(radixLoad);
            if (radixLoad.Opcode == Opcode.LoadConst_I32)
                Assert.Equal(16, radixLoad.Operand0);
            else
            {
                Assert.Equal(Opcode.LoadConst_F64, radixLoad.Opcode);
                Assert.Equal(16d, Assert.IsType<double>(radixLoad.OperandObject));
            }
            radixLiteral = 16;
        }
        var ret = block.Instructions[callIndex + 1];
        Assert.Equal(Opcode.Return, ret.Opcode);

        TsValue[] args = [TsValue.FromString(input)];
        var exactInput = Assert.IsType<TsStringValue>(args[0]);
        Assert.Equal(input, exactInput.Value);
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var actual = (result as TsFloat64Value)?.Value;
        var culture = CultureInfo.InvariantCulture;
        string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", culture);
        output.WriteLine("C122 parseInt undefined " + JsonSerializer.Serialize(new
        {
            row, source, parserCount, binderCount, target, opcode = call.Opcode.ToString(), argc = call.Operand1,
            inputOp = load.Opcode.ToString(), inputArg = load.Operand0, inputType = exactInput.GetType().Name,
            inputValue = exactInput.Value, radixOp = radixLoad?.Opcode.ToString(), radixLiteral,
            returnOp = ret.Opcode.ToString(), escaped = escaped?.GetType().FullName,
            actualType = result?.GetType().Name, actual = actual?.ToString("R", culture),
            actualBits = actual is double observed ? Bits(observed) : null,
            expectedType = nameof(TsFloat64Value), expected = expected.ToString("R", culture), expectedBits = Bits(expected)
        }));
        Assert.Null(escaped);
        double numeric = Assert.IsType<TsFloat64Value>(result).Value;
        Assert.True(double.IsNaN(expected) ? double.IsNaN(numeric) : Bits(numeric) == Bits(expected),
            $"C122_PARSEINT_UNDEFINED_RADIX_RESULT_MISMATCH {row}: expected {expected.ToString("R", culture)} [{Bits(expected)}]; actual {numeric.ToString("R", culture)} [{Bits(numeric)}]");
    }
}
