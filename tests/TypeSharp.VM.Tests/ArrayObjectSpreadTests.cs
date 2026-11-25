using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayObjectSpreadTests(ITestOutputHelper output)
{
    [Fact]
    public void ObjectSpread_CopiesOnlyPresentArrayIndexesAndPreservesOverrides()
    {
        const string source = """
            function main(row: any[], earlier: int32, later: int32, marker: int32): any {
                return { [0]: earlier, ...row, [2]: later, tail: marker };
            }
            """;

        var tokens = new Lexer(source).Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var syntaxMain = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(syntax.Members));
        Assert.Equal("main", syntaxMain.Name);
        Assert.IsType<ArrayTypeSyntax>(syntaxMain.Parameters[0].TypeAnnotation);
        var syntaxReturn = Assert.IsType<ReturnStatementSyntax>(
            Assert.Single(Assert.IsType<BlockStatementSyntax>(syntaxMain.Body).Statements));
        var syntaxLiteral = Assert.IsType<ObjectLiteralExpressionSyntax>(syntaxReturn.Value);
        var syntaxSpread = Assert.Single(syntaxLiteral.Properties.OfType<ObjectSpreadPropertySyntax>());
        Assert.Equal("row", Assert.IsType<IdentifierExpressionSyntax>(syntaxSpread.Value).Name);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);
        var boundMain = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        var rowParameter = boundMain.Symbol.Parameters[0];
        var boundReturn = Assert.IsType<BoundReturnStatement>(
            Assert.Single(Assert.IsType<BoundBlockStatement>(boundMain.Body).Statements));
        var boundLiteral = Assert.IsType<BoundObjectLiteralExpression>(boundReturn.Value);
        var boundSpread = Assert.Single(boundLiteral.Properties, property => property.IsSpread);
        var operand = Assert.IsType<BoundVariableExpression>(boundSpread.Value);
        Assert.Same(rowParameter, operand.Symbol);
        var arrayType = Assert.IsType<TsArrayType>(operand.Type);
        Assert.Same(TsType.Any, arrayType.ElementType);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, function => function.Name == "main");
        var irOps = irMain.Blocks.SelectMany(block => block.Instructions).ToArray();
        var irCopy = Assert.Single(irOps, instruction => instruction.Opcode == Opcode.CopyObjectFields);
        Assert.Equal(Opcode.LoadArg, irOps[Array.IndexOf(irOps, irCopy) - 1].Opcode);
        Assert.Equal(0, irOps[Array.IndexOf(irOps, irCopy) - 1].Operand0);

        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, BytecodeCompiler.Compile(ir));
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var code = Assert.Single(restored.Functions, function => function.Name == "main").Instructions;
        var copySites = 0;
        for (var ip = 0; ip < code.Length;)
        {
            var opcode = code[ip];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format));
            var instructionSize = 1 + format.OperandBytes;
            Assert.InRange(instructionSize, 1, code.Length - ip);
            if (opcode == Opcodes.CopyObjectFields)
                copySites++;
            ip += instructionSize;
        }
        Assert.Equal(1, copySites);

        var array = new TsArray();
        array.Add(TsValue.FromInt32(10));
        array.SetHole(1);
        array.Add(TsValue.FromInt32(30));
        array.Add(TsValue.Void);
        array.Add(TsValue.FromInt32(50));
        var input = new TsArrayValue(array);
        string[] expectedSlots = ["int32:10", "hole", "int32:30", "undefined", "int32:50"];
        var before = Snapshot(array);
        Assert.Equal(expectedSlots, before);

        TsValue? value = null;
        var executeError = Record.Exception(() =>
        {
            value = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, "main",
                [input, TsValue.FromInt32(99), TsValue.FromInt32(77), TsValue.FromInt32(5)]);
        });
        var after = Snapshot(array);
        var result = value as TsObjectValue;
        var fields = result?.Value.Fields.Select(pair => new OwnField(pair.Key, Describe(pair.Value))).ToArray() ?? [];
        OwnField[] expected =
        [
            new("0", "int32:10"),
            new("2", "int32:77"),
            new("3", "undefined"),
            new("4", "int32:50"),
            new("tail", "int32:5"),
        ];

        var expectedKeys = expected.Select(field => field.Key).ToArray();
        var keys = fields.Select(field => field.Key).ToArray();
        var missing = expectedKeys.Except(keys, StringComparer.Ordinal).ToArray();
        var unexpected = keys.Except(expectedKeys, StringComparer.Ordinal).ToArray();
        var mismatches = new List<string>();
        if (executeError is not null)
            mismatches.Add("ExecuteException");
        if (result?.Value.TypeName != "Object")
            mismatches.Add("ResultNotOrdinaryObject");
        if (!after.SequenceEqual(expectedSlots))
            mismatches.Add("SourceShapeChanged");
        foreach (var key in missing)
            mismatches.Add("MissingOwnField:" + key);
        foreach (var key in unexpected)
            mismatches.Add("UnexpectedOwnField:" + key);
        if (!keys.Where(key => !unexpected.Contains(key)).SequenceEqual(expectedKeys.Where(key => !missing.Contains(key))))
            mismatches.Add("OwnFieldOrder");
        foreach (var field in expected)
        {
            if (result?.Value.Fields.TryGetValue(field.Key, out var actual) == true && Describe(actual) != field.Value)
                mismatches.Add("WrongOwnFieldValue:" + field.Key);
        }

        output.WriteLine("C198 " + JsonSerializer.Serialize(new
        {
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count(),
            boundSpreadType = arrayType.Name,
            irCopySites = 1,
            bytecodeCopySites = copySites,
            executeException = executeError?.GetType().FullName,
            resultType = result?.Value.TypeName,
            sourceBefore = before,
            sourceAfter = after,
            ownFields = fields,
            missingKeys = missing,
            unexpectedKeys = unexpected,
            mismatches,
        }));
        Assert.True(mismatches.Count == 0, string.Join(", ", mismatches));
    }

    private static string[] Snapshot(TsArray array) =>
        Enumerable.Range(0, array.Count).Select(i => array.IsHole(i) ? "hole" : Describe(array.Get(i))).ToArray();

    private static string Describe(TsValue value) => value switch
    {
        TsInt32Value i => "int32:" + i.Value.ToString(CultureInfo.InvariantCulture),
        _ when ReferenceEquals(value, TsValue.Void) => "undefined",
        _ => "other:" + value.GetType().Name,
    };

    private sealed record OwnField(string Key, string Value);
}
