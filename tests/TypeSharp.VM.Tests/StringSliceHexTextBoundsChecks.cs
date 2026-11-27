using System.Buffers.Binary;
using System.Globalization;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Tests;

internal static class StringSliceHexTextBoundsChecks
{
    private const string Target = "String::slice";
    private const string Receiver = "abcdef";
    private const string File = "c296-string-slice.ts";

    private sealed record Row(string Name, string Receiver, object? Start, object? End, string Expected);

    private static readonly Row[] Regressions =
    [
        new("hex-end", Receiver, 1, "0x4", "bcd"),
        new("hex-start", Receiver, "0X2", 4, "cd"),
        new("uppercase-bom-oversize-end", Receiver, 1, "\uFEFF0XFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF\u2029", "bcdef"),
        new("oversize-start-omitted", Receiver, "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", null, ""),
        new("utf16-pair", "a\uD83D\uDE00z", "0x1", "0X3", "\uD83D\uDE00"),
        new("long-leading-zero-end", Receiver, 1, "0x000000000000000000000000000000004", "bcd")
    ];

    private static readonly Row[] Controls =
    [
        new("malformed-after-saturation", Receiver, 1, "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFg", ""),
        new("signed-hex", Receiver, 1, "+0x4", ""),
        new("prefix-only", Receiver, 1, "0x", ""),
        new("decimal-relative", Receiver, "-4.9", "-1.7", "cde"),
        new("native-relative", Receiver, -2, null, "ef"),
        new("native-reverse", Receiver, 4, 2, ""),
        new("explicit-undefined-end", Receiver, 1, TsValue.Void, "bcdef"),
        new("explicit-undefined-start", Receiver, TsValue.Void, 3, "abc"),
        new("omitted-bounds", Receiver, null, null, "abcdef"),
        new("zero-hex-end", Receiver, 1, "0x0", ""),
        new("empty-receiver", "", 1, "0x4", "")
    ];

    internal static void CheckControls(Action<string> log) => Check(Controls, log);

    internal static void CheckRegressions(Action<string> log) => Check(Regressions, log);

    internal sealed class BoundsMismatchException(string[] differences)
        : Exception("C296_STRING_SLICE_HEX_TEXT_BOUNDS_RESULT\n" + string.Join("\n", differences))
    {
        internal IReadOnlyList<string> Differences { get; } = differences;
    }

    private static void Check(Row[] rows, Action<string> log)
    {
        var differences = new List<string>();
        foreach (var row in rows)
        {
            var args = new List<TsValue> { new TsStringValue(row.Receiver) };
            if (row.Start is { } start)
                args.Add(MakeBound(start));
            if (row.End is { } end)
            {
                Require(row.Start is not null, "end without start slot");
                args.Add(MakeBound(end));
            }

            var module = CompileRoute(args.Count);
            var actual = RequireType<TsStringValue>(
                new TypeSharp.VM.Interpreter.Interpreter().Execute(module, "main", args.ToArray()),
                "result string");
            Require(actual.ValueType == TsValueType.String, "string result type tag");
            var line = $"{row.Name} expected={Units(row.Expected)} actual={Units(actual.Value)}";
            log($"C296_ROW {line}");
            if (!string.Equals(actual.Value, row.Expected, StringComparison.Ordinal))
                differences.Add(line);
        }

        if (differences.Count != 0)
            throw new BoundsMismatchException(differences.ToArray());
    }

    private static TsValue MakeBound(object value) => value switch
    {
        string text => new TsStringValue(text),
        int integer => new TsInt32Value(integer),
        TsVoid when ReferenceEquals(value, TsValue.Void) => TsValue.Void,
        _ => throw new InvalidOperationException("Unknown C296 fixture input.")
    };

    private static BytecodeModule CompileRoute(int argc)
    {
        string source = argc switch
        {
            1 => "function main(s: string): string { return s.slice(); }",
            2 => "function main(s: string, start: any): string { return s.slice(start); }",
            3 => "function main(s: string, start: any, end: any): string { return s.slice(start, end); }",
            _ => throw new InvalidOperationException("Unknown C296 fixture arity.")
        };
        var tokens = new Lexer(source, File).Tokenize();
        Require(tokens.All(token => token.Kind != TokenKind.Error), "lexer errors");
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Require(parser.Diagnostics.Count == 0, "parser diagnostics");
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Require(!binder.Diagnostics.All.Any(), "binder diagnostics");

        var main = RequireType<BoundFunctionDeclaration>(bound.Members.Single(), "sole bound main");
        Require(main.Symbol.Name == "main" && ReferenceEquals(main.Symbol.Type, TsType.String), "bound main signature");
        string[] names = ["s", "start", "end"];
        var parameters = main.Symbol.Parameters.ToArray();
        Require(parameters.Length == argc, "bound parameter count");
        for (int slot = 0; slot < argc; slot++)
        {
            Require(parameters[slot].Name == names[slot], "bound parameter order");
            Require(ReferenceEquals(parameters[slot].Type, slot == 0 ? TsType.String : TsType.Any),
                "bound parameter type");
        }

        var body = RequireType<BoundBlockStatement>(main.Body, "bound main body");
        var ret = RequireType<BoundReturnStatement>(body.Statements.Single(), "bound return");
        var call = RequireType<BoundCallExpression>(ret.Value, "bound call");
        var member = RequireType<BoundMemberAccessExpression>(call.Callee, "bound member");
        var method = RequireType<MethodSymbol>(member.Member, "bound builtin method");
        Require(!call.IsNullConditional && !member.IsNullConditional
            && ReferenceEquals(call.Type, TsType.String) && ReferenceEquals(method.Type, TsType.String)
            && method.Name == "slice" && method.DeclaringClassName == "String" && method.RuntimeName is null
            && !method.IsStatic && method.HasDynamicSignature, "ordinary String.slice builtin");
        Require(call.Arguments.Count == argc - 1, "bound source argument count");
        CheckParameter(member.Object, parameters[0]);
        for (int slot = 1; slot < argc; slot++)
            CheckParameter(call.Arguments[slot - 1], parameters[slot]);

        var ir = new IRGenerator().Generate(bound);
        var irMain = ir.Functions.Single();
        Require(irMain.Name == "main" && ReferenceEquals(irMain.ReturnType, TsType.String)
            && irMain.Parameters.Count == argc, "IR main signature");
        for (int slot = 0; slot < argc; slot++)
            Require(irMain.Parameters[slot].Name == names[slot]
                && ReferenceEquals(irMain.Parameters[slot].Type, parameters[slot].Type), "IR parameter order/type");

        var code = irMain.Blocks.Single().Instructions;
        Require(code.Count == argc + 2, "IR instruction count");
        for (int slot = 0; slot < argc; slot++)
            Require(code[slot].Opcode == Opcode.LoadArg && code[slot].Operand0 == slot, "IR argument slot");
        Require(code[^2].Opcode == Opcode.Call && code[^2].OperandObject is string target && target == Target
            && code[^2].Operand1 == argc && code[^1].Opcode == Opcode.Return, "IR target/arity/return");

        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);
        var function = module.Functions.Single();
        Require(function.Name == "main" && function.ParameterCount == argc, "bytecode main signature");
        var bytes = function.Instructions;
        Require(bytes.Length == argc * 5 + 10 && function.StringConstants.Single() == Target,
            "bytecode length and builtin constant");
        for (int slot = 0; slot < argc; slot++)
        {
            int offset = slot * 5;
            Require(bytes[offset] == Opcodes.LoadArg && ReadInt32(bytes, offset + 1) == slot,
                "bytecode argument slot");
        }

        int callOffset = argc * 5;
        Require(bytes[callOffset] == Opcodes.Call && ReadInt32(bytes, callOffset + 1) == 0
            && ReadInt32(bytes, callOffset + 5) == argc && bytes[^1] == Opcodes.Return,
            "bytecode named target/arity/return");
        return module;
    }

    private static void CheckParameter(BoundNode node, ParameterSymbol parameter)
    {
        var variable = RequireType<BoundVariableExpression>(node, "bound parameter expression");
        Require(ReferenceEquals(variable.Symbol, parameter) && ReferenceEquals(variable.Type, parameter.Type),
            "bound argument identity/type");
    }

    private static int ReadInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));

    private static string Units(string value) =>
        "[" + string.Join(",", value.Select(unit => ((int)unit).ToString("X4", CultureInfo.InvariantCulture))) + "]";

    private static T RequireType<T>(object? value, string assertion) where T : class =>
        value is T typed ? typed : throw new InvalidOperationException("C296 route: " + assertion);

    private static void Require(bool condition, string assertion)
    {
        if (!condition)
            throw new InvalidOperationException("C296 route: " + assertion);
    }
}
