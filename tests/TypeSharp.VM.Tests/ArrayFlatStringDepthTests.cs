using System.Globalization;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFlatStringDepthTests
{
    private const string Dense = "[1,[2,[3]],4]";
    private const string Sparse = "[H,[H,U,2],3]";
    private const string One = "[1,2,[3],4]";
    private const string Two = "[1,2,3,4]";
    internal const string DepthError = "Array.flat depth must be a finite number or undefined";

    [Theory]
    [InlineData("one", "1", Dense, One, 1, 2)]
    [InlineData("signed-fraction", "  +1.9  ", Dense, One, 1, 2)]
    [InlineData("exponent", "1e0", Dense, One, 1, 2)]
    [InlineData("two", "2", Dense, Two, -1, -1)]
    [InlineData("zero", "0", Dense, Dense, 0, 1)]
    [InlineData("negative", "-2.8", Dense, Dense, 0, 1)]
    [InlineData("empty", "", Dense, Dense, 0, 1)]
    [InlineData("whitespace", "   ", Dense, Dense, 0, 1)]
    [InlineData("nan", "NaN", Dense, Dense, 0, 1)]
    [InlineData("non-number", "junk", Dense, Dense, 0, 1)]
    [InlineData("non-number-suffix", "2junk", Dense, Dense, 0, 1)]
    [InlineData("large-finite", "1e10", Dense, Two, -1, -1)]
    [InlineData("sparse-one", "1", Sparse, "[U,2,3]", -1, -1)]
    [InlineData("sparse-zero", "0", Sparse, "[[H,U,2],3]", 0, 0)]
    public void Flat_UsesDecimalStringDepth(string row, string depth, string input, string expected,
        int survivorLevel, int survivorResultIndex)
        => Check(row, $"'{depth}'", input, expected, survivorLevel, survivorResultIndex);

    internal static void Check(string row, string expression, string input, string expected,
        int survivorLevel, int survivorResultIndex, Action<string>? report = null)
    {
        var child = input == Dense ? ArrayOf(TsValue.FromInt32(2), ArrayOf(TsValue.FromInt32(3))) :
            ArrayOf(null, TsValue.Void, TsValue.FromInt32(2));
        var receiver = input == Dense ? ArrayOf(TsValue.FromInt32(1), child, TsValue.FromInt32(4)) :
            ArrayOf(null, child, TsValue.FromInt32(3));
        Require(Snapshot(receiver.Value) == input, row + ": receiver fixture");
        var survivor = survivorLevel switch
        {
            0 => child,
            1 => child.Value.Get(1) as TsArrayValue ?? throw new InvalidOperationException("Invalid survivor fixture"),
            _ => null
        };
        var source = $"function main(a: any[]): any[] {{ return a.flat({expression}); }}";
        var tokens = new Lexer(source).Tokenize();
        Require(tokens.All(token => token.Kind != TokenKind.Error), row + ": lexer diagnostics");
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Require(parser.Diagnostics.Count == 0 && binder.Diagnostics.All.Count == 0, row + ": parser/binder diagnostics");
        var ir = new IRGenerator().Generate(bound);
        var main = ir.Functions.Single(f => f.Name == "main");
        var call = main.Blocks.SelectMany(b => b.Instructions).Single(i => i.Opcode == Opcode.Call);
        Require(call.OperandObject is string target && target == "Array::flat" &&
            call.Operand1 == (expression.Length == 0 ? 1 : 2), row + ": IR target/argc");
        var bytecode = BytecodeCompiler.Compile(ir);
        report?.Invoke($"LOWER {row} lexer/parser/binder=0/0/0 target={call.OperandObject} argc={call.Operand1}");
        TsValue? value;
        try
        {
            value = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", [receiver]);
        }
        finally
        {
            Require(Snapshot(receiver.Value) == input && ReferenceEquals(receiver.Value.Get(1), child),
                row + ": receiver changed");
            report?.Invoke($"RECEIVER {row} {input}");
        }

        Require(expected != "reject", row + ": expected depth rejection");
        var result = value as TsArrayValue ?? throw new InvalidOperationException(row + ": expected result array");
        Require(!ReferenceEquals(receiver, result) && !ReferenceEquals(receiver.Value, result.Value),
            row + ": result wrapper/backing aliased");
        string actual = Snapshot(result.Value);
        Require(actual == expected, row + $": expected {expected}, got {actual}");
        if (survivor is not null)
        {
            var observed = result.Value.Get(survivorResultIndex) as TsArrayValue;
            Require(ReferenceEquals(survivor, observed) && ReferenceEquals(survivor.Value, observed?.Value),
                row + ": surviving nested identity changed");
        }
        report?.Invoke($"SHAPE {row} {actual} fresh=true survivor={(survivor is null ? "none" : "same")}");
    }

    private static TsArrayValue ArrayOf(params TsValue?[] slots)
    {
        var result = new TsArray();
        foreach (var slot in slots)
        {
            if (slot is null) result.SetHole(result.Count);
            else result.Add(slot);
        }
        return new TsArrayValue(result);
    }

    private static string Snapshot(TsArray array) => "[" + string.Join(",",
        Enumerable.Range(0, array.Count).Select(i => array.IsHole(i) ? "H" : array.Get(i) switch
        {
            TsArrayValue child => Snapshot(child.Value),
            TsInt32Value number => number.Value.ToString(CultureInfo.InvariantCulture),
            TsVoid => "U",
            var unexpected => throw new InvalidOperationException($"Unexpected array slot: {unexpected.GetType()}")
        })) + "]";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
