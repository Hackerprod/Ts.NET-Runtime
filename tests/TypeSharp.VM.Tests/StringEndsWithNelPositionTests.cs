using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringEndsWithNelPositionTests(ITestOutputHelper output)
{
    private const string Source = "function main(s: string, suffix: string, p: string): boolean { return s.endsWith(suffix, p); }";
    private const string Target = "String::endsWith";
    private const string InstructionBytes = "12000000001201000000120200000073000000000300000075";

    private sealed record Row(string Name, string Suffix, string Position, bool Expected, bool Control);

    [Fact]
    public void StringEndsWith_RejectsBoundaryNelInStringEndPosition()
    {
        Row[] rows =
        [
            new("numeric-text", "b", "2", true, Control: true),
            new("ascii-space", "b", " 2 ", true, Control: true),
            new("not-numeric", "b", "no", false, Control: true),
            new("empty-needle", "", "\u00852", true, Control: true),
            new("interior-only-nel", "b", "2\u00852", false, Control: true),
            new("trailing-under-ecma-whitespace", "b", " 2\u0085\u2028", false, Control: false),
            new("primary-leading-nel", "b", "\u00852", false, Control: false),
        ];

        var tokens = new TypeSharp.Syntax.Lexer(Source).Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var boundMain = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", boundMain.Symbol.Name);
        Assert.Same(TsType.Bool, boundMain.Symbol.Type);
        Assert.Equal(new[] { "s", "suffix", "p" }, boundMain.Symbol.Parameters.Select(p => p.Name).ToArray());
        Assert.All(boundMain.Symbol.Parameters, p => Assert.Same(TsType.String, p.Type));

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = Assert.Single(irMain.Blocks).Instructions;
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.LoadArg, Opcode.LoadArg, Opcode.Call, Opcode.Return },
            ops.Select(op => op.Opcode).ToArray());
        var irArgs = ops.Take(3).Select(op => op.Operand0).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, irArgs);
        string target = Assert.IsType<string>(ops[3].OperandObject);
        int argc = ops[3].Operand1;
        Assert.Equal(Target, target);
        Assert.Equal(3, argc);

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiledMain = Assert.Single(bytecode.Functions, function => function.Name == "main");
        var compiled = ReadRoute(compiledMain);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, bytecode);
        stream.Position = 0;
        var restoredModule = BytecodeSerializer.Deserialize(stream);
        var restoredMain = Assert.Single(restoredModule.Functions, function => function.Name == "main");
        var restored = ReadRoute(restoredMain);
        Assert.Equal(compiled, restored);

        var parameters = boundMain.Symbol.Parameters.Select(p => new { p.Name, type = p.Type.ToString() }).ToArray();
        var irOps = ops.Select(op => op.Opcode.ToString()).ToArray();
        var measured = rows.Select(row =>
        {
            TsValue[] inputs = [new TsStringValue("abcdef"), new TsStringValue(row.Suffix), new TsStringValue(row.Position)];
            var vm = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            var escaped = Record.Exception(() => { result = vm.Execute(restoredModule, restoredMain.Name, inputs); });
            output.WriteLine("C215_STRING_ENDSWITH_NEL_POSITION " + JsonSerializer.Serialize(new
            {
                row = row.Name, source = Source, entry = restoredMain.Name, parserCount, binderCount,
                parameters, returnType = boundMain.Symbol.Type.ToString(), ir = irOps, irArgs, target, argc,
                compiled, restored,
                inputs = inputs.Select(input => new
                {
                    type = input.GetType().Name,
                    tag = input.ValueType.ToString(),
                    units = Units(((TsStringValue)input).Value)
                }).ToArray(),
                actualType = result?.GetType().Name,
                actualTag = result?.ValueType.ToString(),
                actual = (result as TsBoolValue)?.Value,
                expected = row.Expected,
                escaped = escaped?.GetType().FullName
            }));
            return (Row: row, Result: result, Escaped: escaped);
        }).ToArray();

        foreach (var (_, result, escaped) in measured)
        {
            Assert.Null(escaped);
            Assert.Equal(TsValueType.Bool, Assert.IsType<TsBoolValue>(result).ValueType);
        }

        var values = measured.Select(item => (Row: item.Row, Actual: ((TsBoolValue)item.Result!).Value)).ToArray();
        foreach (var (row, actual) in values.Where(item => item.Row.Control))
            Assert.True(row.Expected == actual, $"Unchanged control {row.Name}: expected {row.Expected}, actual {actual}");

        var mismatches = values.Where(item => !item.Row.Control && item.Row.Expected != item.Actual)
            .Select(item => $"{item.Row.Name}: expected {item.Row.Expected}, actual {item.Actual}").ToArray();
        Assert.True(mismatches.Length == 0, $"C215_STRING_ENDSWITH_NEL_POSITION_RESULT {string.Join("; ", mismatches)}");
    }

    private sealed record Route(string Instructions, int ParameterCount, int ReceiverArg, int SuffixArg,
        int PositionArg, int TargetIndex, string Target, int Argc, int StringCount);

    private static Route ReadRoute(BytecodeFunction function)
    {
        var bytes = function.Instructions;
        string hex = Convert.ToHexString(bytes);
        Assert.Equal(InstructionBytes, hex);
        Assert.Equal(3, function.ParameterCount);

        int offset = 0;
        int[] args = new int[3];
        for (int i = 0; i < args.Length; i++)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[offset]);
            args[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
            offset += OpcodeFormats.InstructionSize(bytes[offset]);
        }
        Assert.Equal(new[] { 0, 1, 2 }, args);

        Assert.Equal(Opcodes.Call, bytes[offset]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4));
        Assert.Equal(0, targetIndex);
        Assert.Equal(3, argc);
        Assert.Single(function.StringConstants);
        string target = function.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        offset += OpcodeFormats.InstructionSize(bytes[offset]);

        Assert.Equal(Opcodes.Return, bytes[offset]);
        Assert.Equal(bytes.Length, offset + OpcodeFormats.InstructionSize(bytes[offset]));
        return new Route(hex, function.ParameterCount, args[0], args[1], args[2], targetIndex, target, argc,
            function.StringConstants.Length);
    }

    private static string Units(string text) =>
        string.Join(" ", text.Select(unit => ((int)unit).ToString("X4", CultureInfo.InvariantCulture)));
}
