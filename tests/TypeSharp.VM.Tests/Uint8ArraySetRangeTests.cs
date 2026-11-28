using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArraySetRangeTests
{
    internal sealed record Row(string Id, string Source, bool Empty, double Offset, string Expected, string Bytes);

    internal static Row[] OrdinaryRows() =>
    [
        new("typed-overrun", "typed", false, 2, "caught-RangeError", "10,20,30"),
        new("array-overrun", "array", false, 2, "caught-RangeError", "10,20,30"),
        new("typed-exact", "typed", false, 1, "undefined", "10,90,91"),
        new("array-exact", "array", false, 1, "undefined", "10,90,91"),
        new("typed-empty-end", "typed", true, 3, "undefined", "10,20,30"),
        new("array-empty-end", "array", true, 3, "undefined", "10,20,30"),
        new("typed-empty-beyond", "typed", true, 4, "caught-RangeError", "10,20,30"),
        new("array-empty-beyond", "array", true, 4, "caught-RangeError", "10,20,30"),
        new("typed-fraction-to-zero", "typed", false, -0.5, "undefined", "90,91,30"),
        new("array-fraction-to-zero", "array", false, -0.5, "undefined", "90,91,30"),
        new("lengthless-primitive-end", "lengthless", false, 3, "undefined", "10,20,30"),
        new("lengthless-primitive-beyond", "lengthless", false, 4, "caught-RangeError", "10,20,30")
    ];

    internal static Row[] NegativeRows() =>
    [
        new("typed-negative", "typed", false, -1, "caught-RangeError", "10,20,30"),
        new("array-negative", "array", false, -1, "caught-RangeError", "10,20,30"),
        new("typed-empty-negative", "typed", true, -1, "caught-RangeError", "10,20,30"),
        new("array-empty-negative", "array", true, -1, "caught-RangeError", "10,20,30")
    ];

    internal static (Func<TsValue?> Invoke, TsUint8ArrayValue Receiver, string Evidence) Prepare(Row row)
    {
        string sourceType = row.Source switch
        {
            "typed" => "Uint8Array",
            "array" => "number[]",
            _ => "any"
        };
        string script = $$"""
            function main(target: Uint8Array, source: {{sourceType}}, offset: number): any {
                try { return target.set(source, offset); }
                catch (error) { return error; }
            }
            """;
        var parser = new Parser(new Lexer(script).Tokenize());
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Uint8Array::set", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(3, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);

        var receiver = new TsUint8ArrayValue([10, 20, 30]);
        byte[] bytes = row.Empty ? [] : [90, 91];
        var plain = new TsArray();
        foreach (byte value in bytes) plain.Add(TsValue.FromInt32(value));
        TsValue source = row.Source switch
        {
            "typed" => new TsUint8ArrayValue(bytes),
            "array" => new TsArrayValue(plain),
            _ => TsValue.FromInt32(123)
        };
        TsValue? Invoke() => new Vm().Execute(bytecode, "main", [receiver, source, TsValue.FromFloat64(row.Offset)]);
        return (Invoke, receiver, $"source={sourceType} parser=0 binder=0 verified call={call.OperandObject}/{call.Operand1}");
    }

    internal static string Outcome(TsValue? result) => result switch
    {
        TsVoid => "undefined",
        TsNull => "null",
        TsObjectValue error when error.Value.TypeName == "RangeError"
            && error.Value.Fields.GetValueOrDefault("name") is TsStringValue { Value: "RangeError" }
            && error.Value.Fields.GetValueOrDefault("message") is TsStringValue message
            && !string.IsNullOrWhiteSpace(message.Value) => "caught-RangeError",
        _ => "unexpected:" + (result?.GetType().Name ?? "CLR-null")
    };

    internal static string Bytes(TsUint8ArrayValue receiver) => string.Join(",", receiver.Value);

    [Fact]
    public void RejectsOutOfBoundsBeforeMutatingReceiver()
    {
        var mismatches = new List<string>();
        foreach (var row in OrdinaryRows())
        {
            var (invoke, receiver, _) = Prepare(row);
            string actual = Outcome(invoke());
            string bytes = Bytes(receiver);
            if (actual != row.Expected || bytes != row.Bytes)
                mismatches.Add($"{row.Id} expected={row.Expected}:{row.Bytes} actual={actual}:{bytes}");
        }
        Assert.True(mismatches.Count == 0, "C310_RANGE " + string.Join("; ", mismatches));
    }

    [Fact]
    public void RejectsNegativeIntegralOffsetsInScriptWithoutWrites()
    {
        foreach (var row in NegativeRows())
        {
            var (invoke, receiver, _) = Prepare(row);
            Assert.Equal(row.Expected, Outcome(invoke()));
            Assert.Equal(row.Bytes, Bytes(receiver));
        }
    }
}
