using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using TsBinder = TypeSharp.Semantics.Binder.Binder;
using TsParser = TypeSharp.Syntax.Parser.Parser;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

internal static class NumberParseIntChecks
{
    private sealed record Row(string Text, TsValue[] Radix, double Expected);
    private static readonly Row[] Rows =
    [
        new("12px", [], 12),
        new("1e3", [], 1),
        new("0x10", [], 16),
        new(" -0", [], BitConverter.Int64BitsToDouble(long.MinValue)),
        new("42", [], 42),
        new("junk12", [], double.NaN),
        new("101", [TsValue.FromInt32(2)], 5),
        new("fftail", [TsValue.FromInt32(16)], 255),
        new("z!", [TsValue.FromInt32(36)], 35),
        new("321", [TsValue.FromInt32(3)], double.NaN),
        new("42", [TsValue.FromInt32(37)], double.NaN),
        new("0x10", [TsValue.Void], 16),
        new("123", [TsValue.FromFloat64(2.5)], 123),
        new("123", [TsValue.FromString("2")], 123)
    ];

    internal static void CheckOrdinarySource()
    {
        foreach (var row in Rows)
        {
            Execute(row, "parseInt", "parseInt", retargetToNumber: false);
            Execute(row, "Number.parseInt", "Number::parseInt", retargetToNumber: false);
        }
    }

    internal static void CheckDormantEntry()
    {
        // This probes the VM table independently of Binder's Number namespace rule.
        // Start with real global-call IR, then change exactly one symbolic target.
        foreach (var row in Rows)
            Execute(row, "parseInt", "parseInt", retargetToNumber: true);
    }

    private static void Execute(Row row, string syntax, string target, bool retargetToNumber)
    {
        string source = row.Radix.Length == 0
            ? $"function main(s: string): number {{ return {syntax}(s); }}"
            : $"function main(s: string, r: any): number {{ return {syntax}(s, r); }}";
        var tokens = new Lexer(source).Tokenize();
        Require(tokens.All(token => token.Kind != TokenKind.Error), "lexer", source);
        var parser = new TsParser(tokens);
        var program = parser.Parse();
        Require(parser.Diagnostics.Count == 0, string.Join(" | ", parser.Diagnostics), source);
        var binder = new TsBinder();
        var bound = binder.Bind(program);
        Require(binder.Diagnostics.All.Count == 0, string.Join(" | ", binder.Diagnostics.All), source);

        var ir = new IRGenerator().Generate(bound);
        var instructions = ir.Functions.Single(function => function.Name == "main").Blocks.Single().Instructions;
        int argc = 1 + row.Radix.Length;
        Require(instructions.Count == argc + 2, "receiver-free call shape", source);
        for (int index = 0; index < argc; index++)
            Require(instructions[index].Opcode == Opcode.LoadArg && instructions[index].Operand0 == index,
                "argument load order", source);
        var call = instructions[argc];
        Require(call.Opcode == Opcode.Call && call.Operand1 == argc && call.OperandObject is string name &&
            name == target && instructions[^1].Opcode == Opcode.Return, "direct call target/arity", source);
        if (retargetToNumber)
            call.OperandObject = "Number::parseInt";

        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);
        var result = new Vm().Execute(module, "main", [TsValue.FromString(row.Text), .. row.Radix]);
        if (result is not TsFloat64Value number)
            throw new InvalidOperationException($"Expected Float64 for {syntax}({row.Text}).");
        double expected = row.Expected;
        double actual = number.Value;
        Require(double.IsNaN(expected) ? double.IsNaN(actual)
            : BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"input='{row.Text}', expected={Describe(expected)}, actual={Describe(actual)}", source);
    }

    private static string Describe(double value) =>
        double.IsNaN(value) ? "NaN" : $"0x{BitConverter.DoubleToInt64Bits(value):X16}";

    private static void Require(bool condition, string detail, string source)
    {
        if (!condition)
            throw new InvalidOperationException($"{detail}: {source}");
    }
}
