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

public sealed class StringLowercaseFinalSigmaTests(ITestOutputHelper output)
{
    private const string Target = "String::toLowerCase";
    private const string InstructionBytes = "120000000073000000000100000075";
    private const string Source = "function main(s: string): string { return s.toLowerCase(); }";
    private sealed record Row(string Name, string Input, string Expected, bool Unchanged = false);

    [Fact]
    public void StringToLowerCase_UsesFinalSigmaInKnownGreekContext()
    {
        // Only the bounded Greek witness and right boundary cases are required to change.
        Row[] rows =
        [
            new("medial", "\u039F\u03A3\u0391", "\u03BF\u03C3\u03B1", Unchanged: true),
            new("standalone", "\u03A3", "\u03C3", Unchanged: true),
            new("following-cased-after-acute", "\u039F\u03A3\u0301\u0391", "\u03BF\u03C3\u0301\u03B1", Unchanged: true),
            new("preceding-space", "\u039F\u0020\u03A3", "\u03BF\u0020\u03C3", Unchanged: true),
            new("following-supplementary-cased", "\u039F\u03A3\uD801\uDC00", "\u03BF\u03C3\uD801\uDC28", Unchanged: true),
            new("unknown-ignorable-before-cased", "\u039F\u03A3\u0027\u0391", "\u03BF\u03C3\u0027\u03B1", Unchanged: true),
            new("final-before-space", "\u039F\u03A3\u0020\u0391", "\u03BF\u03C2\u0020\u03B1"),
            new("acute-both-sides", "\u039F\u0301\u03A3\u0301", "\u03BF\u0301\u03C2\u0301"),
            new("adjacent-sigmas-and-dotted-i", "\u0130\u0020\u03A3\u03A3", "\u0069\u0307\u0020\u03C3\u03C2"),
            new("primary", "\u039F\u03A3", "\u03BF\u03C2"),
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
        Assert.Same(TsType.String, boundMain.Symbol.Type);
        var parameter = Assert.Single(boundMain.Symbol.Parameters);
        Assert.Equal("s", parameter.Name);
        Assert.Same(TsType.String, parameter.Type);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = Assert.Single(irMain.Blocks).Instructions;
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }, ops.Select(op => op.Opcode).ToArray());
        int receiverArg = ops[0].Operand0;
        string target = Assert.IsType<string>(ops[1].OperandObject);
        int argc = ops[1].Operand1;
        Assert.Equal(0, receiverArg);
        Assert.Equal(Target, target);
        Assert.Equal(1, argc);

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

        var irOps = ops.Select(op => op.Opcode.ToString()).ToArray();
        var measured = rows.Select(row =>
        {
            var input = new TsStringValue(row.Input);
            var vm = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            var escaped = Record.Exception(() => { result = vm.Execute(restoredModule, restoredMain.Name, new TsValue[] { input }); });
            var actual = (result as TsStringValue)?.Value;

            output.WriteLine("C214_STRING_LOWERCASE_FINAL_SIGMA " + JsonSerializer.Serialize(new
            {
                row = row.Name, source = Source, entry = restoredMain.Name, parserCount, binderCount,
                parameter = parameter.Name, parameterType = parameter.Type.ToString(),
                returnType = boundMain.Symbol.Type.ToString(), ir = irOps, receiverArg, target, argc,
                compiled, restored, inputType = input.GetType().Name, inputTag = input.ValueType.ToString(),
                input = Units(input.Value), actualType = result?.GetType().Name,
                actualTag = result?.ValueType.ToString(), actual = actual is null ? null : Units(actual),
                expected = Units(row.Expected), escaped = escaped?.GetType().FullName
            }));
            return (Row: row, Result: result, Escaped: escaped);
        }).ToArray();

        foreach (var (_, result, escaped) in measured)
        {
            Assert.Null(escaped);
            Assert.Equal(TsValueType.String, Assert.IsType<TsStringValue>(result).ValueType);
        }

        var values = measured.Select(item => (Row: item.Row, Actual: ((TsStringValue)item.Result!).Value)).ToArray();
        foreach (var (row, actual) in values.Where(item => item.Row.Unchanged))
            Assert.True(string.Equals(row.Expected, actual, StringComparison.Ordinal),
                $"Unchanged control {row.Name}: expected [{Units(row.Expected)}], actual [{Units(actual)}]");

        var mismatches = values.Where(item => !item.Row.Unchanged &&
            !string.Equals(item.Row.Expected, item.Actual, StringComparison.Ordinal))
            .Select(item => $"{item.Row.Name}: expected [{Units(item.Row.Expected)}], actual [{Units(item.Actual)}]").ToArray();
        Assert.True(mismatches.Length == 0,
            $"C214_STRING_LOWERCASE_FINAL_SIGMA_RESULT {string.Join("; ", mismatches)}");
    }

    private sealed record Route(string Instructions, int ParameterCount, int ReceiverArg, int TargetIndex,
        string Target, int Argc, int StringCount);

    private static Route ReadRoute(BytecodeFunction function)
    {
        var bytes = function.Instructions;
        string hex = Convert.ToHexString(bytes);
        Assert.Equal(InstructionBytes, hex);
        Assert.Equal(1, function.ParameterCount);

        Assert.Equal(Opcodes.LoadArg, bytes[0]);
        int callOffset = OpcodeFormats.InstructionSize(bytes[0]);
        Assert.Equal(Opcodes.Call, bytes[callOffset]);
        int returnOffset = callOffset + OpcodeFormats.InstructionSize(bytes[callOffset]);
        Assert.Equal(Opcodes.Return, bytes[returnOffset]);
        Assert.Equal(bytes.Length, returnOffset + OpcodeFormats.InstructionSize(bytes[returnOffset]));

        int receiverArg = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4));
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 1, 4));
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 5, 4));
        Assert.Equal(0, receiverArg);
        Assert.Equal(0, targetIndex);
        Assert.Equal(1, argc);
        Assert.Single(function.StringConstants);
        string target = function.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        return new Route(hex, function.ParameterCount, receiverArg, targetIndex, target, argc, function.StringConstants.Length);
    }

    private static string Units(string text) =>
        string.Join(" ", text.Select(unit => ((int)unit).ToString("X4", CultureInfo.InvariantCulture)));
}
