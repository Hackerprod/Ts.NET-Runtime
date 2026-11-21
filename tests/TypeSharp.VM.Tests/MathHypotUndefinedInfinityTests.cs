using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathHypotUndefinedInfinityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("positive-infinity-then-undefined", "x, undefined", "7FF0000000000000", "0 Void", "7FF0000000000000")]
    [InlineData("undefined-then-negative-infinity", "undefined, x", "FFF0000000000000", "Void 0", "7FF0000000000000")]
    [InlineData("finite-with-undefined", "x, undefined, y", "4008000000000000 4010000000000000", "0 Void 1", "NaN")]
    [InlineData("undefined-only", "undefined", "", "Void", "NaN")]
    [InlineData("finite-norm", "x, y", "4008000000000000 4010000000000000", "0 1", "4014000000000000")]
    [InlineData("numeric-nan-with-infinity", "x, y", "7FF8000000000000 7FF0000000000000", "0 1", "7FF0000000000000")]
    public void Hypot_HandlesUndefinedAndInfinity(string row, string arguments, string argumentBits, string expectedLoads, string expected)
    {
        static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16");
        var bits = argumentBits.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = bits.Select(value => TsValue.FromFloat64(BitConverter.Int64BitsToDouble(
            unchecked((long)Convert.ToUInt64(value, 16))))).ToArray();
        var parameters = string.Join(", ", new[] { "x: number", "y: number" }.Take(args.Length));
        var source = $"function main({parameters}): number {{ return Math.hypot({arguments}); }}";
        output.WriteLine($"C118 row={row}; source={source}");
        for (int i = 0; i < args.Length; i++)
        {
            var wrapped = Assert.IsType<TsFloat64Value>(args[i]);
            output.WriteLine($"C118 row={row}; slot={i}; wrapper={wrapped.GetType().Name}; tag={wrapped.ValueType}; bits={Bits(wrapped.Value)}");
            Assert.Equal(TsValueType.Float64, wrapped.ValueType);
            Assert.Equal(bits[i], Bits(wrapped.Value));
        }

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        output.WriteLine($"C118 row={row}; parser={parser.Diagnostics.Count()}; binder={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is Opcode.CallVirt or Opcode.CallDynamic);
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var block = Assert.Single(main.Blocks, item => item.Instructions.Contains(call));
        var local = block.Instructions.ToArray();
        var index = Array.IndexOf(local, call);
        var slots = expectedLoads.Split(' ');
        var target = Assert.IsType<string>(call.OperandObject);
        output.WriteLine($"C118 row={row}; target={target}; argc={call.Operand1}; call-index={index}");
        Assert.Equal("Math::hypot", target);
        Assert.Equal(slots.Length, call.Operand1);
        Assert.True(index >= slots.Length && index + 1 < local.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            var position = index - slots.Length + i;
            var load = local[position];
            output.WriteLine($"C118 row={row}; load-index={position}; opcode={load.Opcode}; operand0={load.Operand0}");
            if (slots[i] == "Void")
                Assert.Equal(Opcode.LoadConst_Void, load.Opcode);
            else
            {
                Assert.Equal(Opcode.LoadArg, load.Opcode);
                Assert.Equal(int.Parse(slots[i]), load.Operand0);
            }
        }
        output.WriteLine($"C118 row={row}; return-index={index + 1}; opcode={local[index + 1].Opcode}");
        Assert.Equal(Opcode.Return, local[index + 1].Opcode);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", args); });
        output.WriteLine($"C118 row={row}; exception={error?.GetType().Name ?? "none"}; result-type={result?.GetType().Name}; tag={result?.ValueType}");
        Assert.Null(error);
        var number = Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(TsValueType.Float64, number.ValueType);
        var actualBits = Bits(number.Value);
        var actual = double.IsNaN(number.Value) ? "NaN" : actualBits;
        output.WriteLine($"C118 row={row}; expected={expected}; actual={actual}; actual-bits={actualBits}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"C118_HYPOT_RESULT_MISMATCH row={row}; expected={expected}; actual={actual}");
    }
}
