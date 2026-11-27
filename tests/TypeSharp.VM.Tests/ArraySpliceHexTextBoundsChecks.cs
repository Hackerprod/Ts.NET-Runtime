using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using VmInterpreter = TypeSharp.VM.Interpreter.Interpreter;
using TsBinder = TypeSharp.Semantics.Binder.Binder;
using TsParser = TypeSharp.Syntax.Parser.Parser;

namespace TypeSharp.VM.Tests;

internal static class ArraySpliceHexTextBoundsChecks
{
    private sealed record Row(string Name, string Arguments, int Arity, string Initial, string Expected);

    private static readonly Row[] Regressions =
    [
        new("hex-start", "'0x1', 2", 3, "1234", "14/23"),
        new("hex-count", "1, '0X2'", 3, "1234", "14/23"),
        new("sparse-hex-pair", "'0x1', '0x2'", 3, "1HUN", "1N/HU"),
        new("trimmed-oversize-count", "' 0X2 ', '0xFFFFFFFFFFFFFFFFFFFF'", 3, "1HUN", "1H/UN"),
        new("end-clamp-insert", "'0xFFFFFFFFFFFFFFFFFFFF', 2, 7, 8", 5, "1HUN", "1HUN78/[]"),
        new("hex-start-omitted-count", "'0x2'", 2, "1HUN", "1H/UN"),
        new("hex-start-undefined-count", "'0x2', undefined, 7", 4, "1HUN", "1H7UN/[]")
    ];

    private static readonly Row[] Controls =
    [
        new("omit-dense", "", 1, "123", "123/[]"),
        new("omit-sparse", "", 1, "1HUN", "1HUN/[]"),
        new("omit-empty", "", 1, "", "[]/[]"),
        new("start-only", "1", 2, "123", "1/23"),
        new("explicit-undefined-start", "undefined", 2, "1HUN", "[]/1HUN"),
        new("explicit-zero-count", "0, 0", 3, "123", "123/[]"),
        new("numeric-replace", "1, 1, 7", 4, "123", "173/2"),
        new("invalid-start-no-partial", "'0xFFFFFFFFz', 1", 3, "1HUN", "HUN/1"),
        new("invalid-count-no-partial", "1, '0xFFFFFFFFz', 7", 4, "1HUN", "17HUN/[]"),
        new("signed-hex-and-octal-out-of-scope", "'+0x2', '0o2', 7", 4, "1HUN", "71HUN/[]"),
        new("binary-and-prefix-only-out-of-scope", "'0b10', '0x', 7", 4, "1HUN", "71HUN/[]"),
        new("legacy-relative-and-truncated-decimal", "'-2', '1.9', 7, 8", 5, "1HUN", "1H78N/U"),
        new("legacy-negative-count", "1, '-2', 7", 4, "1HUN", "17HUN/[]"),
        new("hex-zeros-are-valid", "'0x0', '0X000', 7", 4, "1HUN", "71HUN/[]"),
        new("empty-array-hex-ends", "'0x1', '0xFFFFFFFFFFFFFFFFFFFF'", 3, "", "[]/[]"),
        new("old-explicit-undefined-count", "1, undefined, 7", 4, "1HUN", "17HUN/[]")
    ];

    internal static void CheckControls(Action<string> log) => Check(Controls, log);

    internal static void CheckRegressions(Action<string> log) => Check(Regressions, log);

    internal sealed class BoundsMismatchException(string[] differences)
        : Exception("C297_ARRAY_SPLICE_HEX_TEXT_BOUNDS_RESULT\n" + string.Join("\n", differences))
    {
        internal IReadOnlyList<string> Differences { get; } = differences;
    }

    private static void Check(Row[] rows, Action<string> log)
    {
        var differences = new List<string>();
        foreach (var row in rows)
        {
            var module = CompileRoute(row);
            var receiver = new TsArray();
            foreach (char slot in row.Initial)
            {
                switch (slot)
                {
                    case 'H': receiver.SetHole(receiver.Count); break;
                    case 'U': receiver.Add(TsValue.Void); break;
                    case 'N': receiver.Add(TsValue.Null); break;
                    default: receiver.Add(TsValue.FromInt32(slot - '0')); break;
                }
            }

            var value = new VmInterpreter().Execute(module, "main", [new TsArrayValue(receiver)]);
            if (value is not TsArrayValue removed)
                throw new InvalidOperationException("C297 route: expected a removed array in " + row.Name);
            Require(!ReferenceEquals(receiver, removed.Value), "non-aliasing removed array", row);
            var actual = Snapshot(receiver) + "/" + Snapshot(removed.Value);
            var line = $"{row.Name} expected={row.Expected} actual={actual}";
            log($"C297_ROW {line}");
            if (!string.Equals(row.Expected, actual, StringComparison.Ordinal))
                differences.Add(line);
        }

        if (differences.Count != 0)
            throw new BoundsMismatchException(differences.ToArray());
    }

    private static BytecodeModule CompileRoute(Row row)
    {
        var source = $"function main(a: any[]): any[] {{ return a.splice({row.Arguments}); }}";
        var tokens = new Lexer(source, "c297-array-splice.ts").Tokenize();
        Require(tokens.All(token => token.Kind != TokenKind.Error), "lexer errors", row);
        var parser = new TsParser(tokens);
        var syntax = parser.Parse();
        Require(parser.Diagnostics.Count == 0, "parser diagnostics", row);
        var binder = new TsBinder();
        var bound = binder.Bind(syntax);
        Require(!binder.Diagnostics.All.Any(), "binder diagnostics", row);

        var ir = new IRGenerator().Generate(bound);
        var main = ir.Functions.Single(function => function.Name == "main");
        var call = main.Blocks.SelectMany(block => block.Instructions)
            .Single(instruction => instruction.Opcode == Opcode.Call);
        Require(call.OperandObject is string target && target == "Array::splice" && call.Operand1 == row.Arity,
            "bound Array::splice target and receiver-inclusive arity", row);
        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);
        return module;
    }

    private static string Snapshot(TsArray array)
    {
        if (array.Count == 0)
            return "[]";
        return new string(Enumerable.Range(0, array.Count).Select(index =>
            array.IsHole(index) ? 'H' : array.Get(index) switch
            {
                TsVoid => 'U',
                TsNull => 'N',
                TsInt32Value n when n.Value is >= 0 and <= 9 => (char)('0' + n.Value),
                _ => throw new InvalidOperationException("C297 route: unexpected array slot type or value.")
            }).ToArray());
    }

    private static void Require(bool condition, string assertion, Row row)
    {
        if (!condition)
            throw new InvalidOperationException($"C297 route: {assertion} in {row.Name}");
    }
}
