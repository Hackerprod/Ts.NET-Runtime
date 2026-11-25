using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayLastIndexOfHexTextPositionTests(ITestOutputHelper output)
{
    private const string Target = "Array::lastIndexOf";

    [Fact]
    public void LastIndexOf_ConvertsExplicitHexTextPosition()
    {
        // C# escapes supply actual UTF-16 code units inside each generated TS quoted literal.
        (string Label, string Input, int Expected)[] rows =
        [
            ("selected", "0x2", 2),
            ("uppercase/ECMA-trim", "\uFEFF0X0002\u00A0", 2),
            ("wide valid", "0xABCDEF0123456789ABCDEF", 4),
            ("decimal fallback", "2", 2),
            ("signed", "+0x2", 0),
            ("no digits", "0x", 0),
            ("trailing junk", "0xABCDEF0123456789ABCDEFjunk", 0),
            ("non-ECMA whitespace", "\u00850x2", 0)
        ];

        var observations = rows.Select(row =>
        {
            var source = $$"""
                function main(): number {
                    const a: any[] = [7, 2, 7, 3, 7];
                    return a.lastIndexOf(7, "{{row.Input}}");
                }
                """;
            var mainResult = CompileRestoreAndExecute(source, row.Input);
            return new
            {
                label = row.Label,
                input = row.Input,
                resultType = mainResult.GetType().FullName,
                resultTag = mainResult.ValueType.ToString(),
                resultValue = mainResult.Value
            };
        }).ToArray();

        output.WriteLine("C205_ARRAY_LASTINDEXOF_HEX_TEXT " + JsonSerializer.Serialize(new
        {
            main = "main",
            target = Target,
            argc = 3,
            rows = observations
        }));

        var mismatches = observations.Select((actual, index) => (Actual: actual, Expected: rows[index].Expected))
            .Where(row => row.Actual.resultValue != row.Expected)
            .Select(row => FormattableString.Invariant(
                $"[{row.Actual.label}] expected {row.Expected}, actual {row.Actual.resultValue}"))
            .ToArray();
        Assert.True(mismatches.Length == 0, "C205_ARRAY_LASTINDEXOF_HEX_TEXT_MISMATCH " + string.Join("; ", mismatches));
    }

    private static TsInt32Value CompileRestoreAndExecute(string source, string input)
    {
        var tokens = new Lexer(source, "c205-array-lastindexof-hex-text.ts").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        Assert.Equal(input, Assert.IsType<string>(Assert.Single(tokens, token => token.Kind == TokenKind.StringLiteral).Value));
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);
        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(TsType.Number, main.Symbol.Type);
        Assert.Empty(main.Symbol.Parameters);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.Single(body.Statements.OfType<BoundReturnStatement>());
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Int32, boundCall.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal(Target, $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}");
        Assert.Same(TsType.Int32, method.Type);
        Assert.Equal(2, boundCall.Arguments.Count);
        var literal = Assert.IsType<BoundLiteralExpression>(boundCall.Arguments[1]);
        Assert.Equal(input, Assert.IsType<string>(literal.Value));

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == main.Symbol.Name);
        var instructions = irMain.Blocks.SelectMany(block => block.Instructions).ToArray();
        var irCall = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal(Target, Assert.IsType<string>(irCall.OperandObject));
        Assert.Equal(3, irCall.Operand1);
        var irLiteral = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.LoadConst_String);
        Assert.Equal(input, Assert.IsType<string>(irLiteral.OperandObject));

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, function => function.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, function => function.Name == irMain.Name);
        Assert.Equal(0, compiledMain.ParameterCount);
        Assert.Equal(0, restoredMain.ParameterCount);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        // The module stores its string pools per function.
        Assert.Equal(compiled.Functions.Length, restored.Functions.Length);
        for (int index = 0; index < compiled.Functions.Length; index++)
        {
            Assert.Equal(compiled.Functions[index].Name, restored.Functions[index].Name);
            Assert.Equal(compiled.Functions[index].StringConstants, restored.Functions[index].StringConstants);
        }

        var (targetIndex, argc) = DecodeDirectCall(restoredMain.Instructions);
        Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
        Assert.Equal(Target, restoredMain.StringConstants[targetIndex]);
        Assert.Equal(3, argc);

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, []);
        return Assert.IsType<TsInt32Value>(result);
    }

    private static (int TargetIndex, int Argc) DecodeDirectCall(byte[] bytes)
    {
        var calls = new List<(int TargetIndex, int Argc)>();
        for (int offset = 0; offset < bytes.Length;)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            if (opcode == Opcodes.Call)
            {
                Assert.Equal(OperandKind.Int32x2, format.Operand);
                calls.Add((
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            offset += width;
        }
        return Assert.Single(calls);
    }
}
