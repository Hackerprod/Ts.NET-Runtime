using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathClz32Tests(ITestOutputHelper output)
{
    private const string WitnessSource = "function main(x: number): number { return Math.clz32(x); }";
    private const string WitnessFile = "c224-math-clz32.ts";
    private const string WitnessMethod = "clz32";

    [Fact]
    public void Clz32_CountsLeadingZerosOfUnsigned32BitNumber()
    {
        string[] mathMembers = ReadMathMembers();
        Control("fround", 16777217d, "4170000010000000", "4170000000000000");
        Control("sqrt", 9d, "4022000000000000", "4008000000000000");

        var (bound, lexerErrors, parserDiagnostics, diagnostics) = Bind(WitnessSource, WitnessFile);
        var (tableEntryCount, templateMemberCount, templateSignature) = Inspect(mathMembers, WitnessMethod);
        Write(new
        {
            kind = "availability",
            source = WitnessSource,
            file = WitnessFile,
            lexerErrors,
            parserDiagnostics,
            diagnostics,
            tableEntryCount,
            templateMemberCount,
            templateSignature
        });
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserDiagnostics);

        DiagnosticProfile[] expectedAbsentDiagnostics =
        [
            new("Error", "None", 0, WitnessFile, 1, 43, 42, "No member 'clz32' on type 'Math'"),
            new("Error", "TS2010", 2010, WitnessFile, 1, 36, 35,
                "Cannot return 'void' from function returning 'number'")
        ];
        bool absent = diagnostics.SequenceEqual(expectedAbsentDiagnostics) && tableEntryCount == 0
            && templateMemberCount == 0 && templateSignature is null;
        bool present = diagnostics.Length == 0 && tableEntryCount == 1
            && templateMemberCount == 1 && templateSignature == Signature(WitnessMethod);
        Assert.True(absent || present, "Unexpected joint binder, VM builtin, and template availability state");
        Assert.True(present, "C224_MATH_CLZ32_SUPPORTED");

        var route = Compile(WitnessSource, WitnessFile, WitnessMethod, bound,
            lexerErrors, parserDiagnostics, diagnostics.Length);
        Run(route, "one", 1d, "3ff0000000000000", "403f000000000000");
        Run(route, "sixteen", 16d, "4030000000000000", "403b000000000000");
        Run(route, "below-boundary", 31d, "403f000000000000", "403b000000000000");
        Run(route, "at-boundary", 32d, "4040000000000000", "403a000000000000");
        Run(route, "zero", 0d, "0000000000000000", "4040000000000000");
        Run(route, "negative-zero", BitConverter.Int64BitsToDouble(long.MinValue),
            "8000000000000000", "4040000000000000");
        Run(route, "high-bit", 2147483648d, "41e0000000000000", "0000000000000000");
        Run(route, "positive-wrap", 4294967312d, "41f0000001000000", "403b000000000000");
        Run(route, "positive-fraction", 31.75d, "403fc00000000000", "403b000000000000");
        Run(route, "negative-one", -1d, "bff0000000000000", "0000000000000000");
        Run(route, "negative-wrap", -4294967280d, "c1effffffe000000", "403b000000000000");
        Run(route, "negative-subunit", -0.75d, "bfe8000000000000", "4040000000000000");
        Run(route, "uint-modulus", 4294967296d, "41f0000000000000", "4040000000000000");
        Run(route, "nan", double.NaN, "NaN", "4040000000000000");
        Run(route, "positive-infinity", double.PositiveInfinity, "7ff0000000000000", "4040000000000000");
        Run(route, "negative-infinity", double.NegativeInfinity, "fff0000000000000", "4040000000000000");

        void Control(string method, double value, string inputBits, string expected)
        {
            string source = WitnessSource.Replace(WitnessMethod, method, StringComparison.Ordinal);
            string file = WitnessFile.Replace(WitnessMethod, method, StringComparison.Ordinal);
            var (control, lexical, parser, binder) = Bind(source, file);
            var (table, count, signature) = Inspect(mathMembers, method);
            var controlRoute = Compile(source, file, method, control, lexical, parser, binder.Length);
            Assert.Equal(1, table);
            Assert.Equal(1, count);
            Assert.Equal(Signature(method), signature);
            Run(controlRoute, method, value, inputBits, expected);
        }
    }

    private static string[] ReadMathMembers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        string declaration = File.ReadAllText(path);
        var math = Assert.Single(Regex.Matches(declaration, @"(?m)^interface[ \t]+Math\b[^\S\n]*\{([^{}]*)\}"));
        return math.Groups[1].Value.Split(';');
    }

    private static (int TableEntryCount, int TemplateMemberCount, string? TemplateSignature) Inspect(
        string[] mathMembers, string method)
    {
        string target = $"Math::{method}";
        int tableEntryCount = Builtins.Names.Count(name => string.Equals(name, target, StringComparison.Ordinal));
        var memberIndices = Enumerable.Range(0, mathMembers.Length)
            .Where(index => Regex.IsMatch(mathMembers[index].TrimStart(), "^" + Regex.Escape(method) + @"\b"))
            .ToArray();
        foreach (int index in memberIndices)
            Assert.True(index < mathMembers.Length - 1, $"Math.{method} must end in a semicolon");
        string? signature = memberIndices.Length == 1 ? mathMembers[memberIndices[0]].Trim() + ";" : null;
        return (tableEntryCount, memberIndices.Length, signature);
    }

    private static string Signature(string method) => $"{method}(x: number): number;";

    private sealed record DiagnosticProfile(
        string severity, string code, int codeNumber, string source, int line, int column, int offset, string message);

    private static (BoundSourceFile Bound, int LexerErrors, int ParserDiagnostics, DiagnosticProfile[] Diagnostics)
        Bind(string source, string file)
    {
        var tokens = new Lexer(source, file).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var parsed = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(parsed);
        var diagnostics = binder.Diagnostics.All.Select(diagnostic => new DiagnosticProfile(
            diagnostic.Severity.ToString(), diagnostic.Code.ToString(), (int)diagnostic.Code,
            diagnostic.Location.Source, diagnostic.Location.Line, diagnostic.Location.Column,
            diagnostic.Location.Offset, diagnostic.Message)).ToArray();
        return (bound, lexerErrors, parser.Diagnostics.Count, diagnostics);
    }

    private sealed record CallRoute(BytecodeModule Module, string Main, string Target, int Argc);

    private CallRoute Compile(string source, string file, string methodName, BoundSourceFile bound,
        int lexerErrors, int parserDiagnostics, int binderDiagnostics)
    {
        string expectedTarget = $"Math::{methodName}";
        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(TsType.Number, function.Symbol.Type);
        var parameter = Assert.Single(function.Symbol.Parameters);
        Assert.Equal("x", parameter.Name);
        Assert.Same(TsType.Number, parameter.Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, boundCall.Type);
        var access = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        var math = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal(math.Name, method.DeclaringClassName);
        Assert.Equal(methodName, method.Name);
        var descriptor = math.ClassType.Methods[methodName];
        Assert.Same(TsType.Number, descriptor.ReturnType);
        Assert.Empty(descriptor.Parameters);
        Assert.Same(TsType.Number, method.Type);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(boundCall.Arguments));
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(argument.Symbol));
        Assert.Same(TsType.Number, argument.Type);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, candidate => candidate.Name == "main");
        Assert.Equal("x", Assert.Single(irMain.Parameters).Name);
        Assert.Collection(Assert.Single(irMain.Blocks).Instructions,
            load =>
            {
                Assert.Equal(Opcode.LoadArg, load.Opcode);
                Assert.Equal(0, load.Operand0);
            },
            call =>
            {
                Assert.Equal(Opcode.Call, call.Opcode);
                Assert.Equal(expectedTarget, Assert.IsType<string>(call.OperandObject));
                Assert.Equal(1, call.Operand1);
            },
            retInstruction => Assert.Equal(Opcode.Return, retInstruction.Opcode));

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, candidate => candidate.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, candidate => candidate.Name == irMain.Name);
        Assert.Equal(compiledMain.ParameterCount, restoredMain.ParameterCount);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);

        var (sequence, slot, targetIndex, argc) = Decode(restoredMain.Instructions);
        Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
        string target = restoredMain.StringConstants[targetIndex];
        string[] decodedSequence = sequence.Select(opcode => opcode switch
        {
            Opcodes.LoadArg => $"LoadArg({slot.ToString(CultureInfo.InvariantCulture)})",
            Opcodes.Call => "Call",
            Opcodes.Return => "Return",
            _ => $"0x{opcode.ToString("x2", CultureInfo.InvariantCulture)}"
        }).ToArray();
        Write(new
        {
            kind = "route",
            source,
            file,
            lexerErrors,
            parserDiagnostics,
            binderDiagnostics,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            sequence = decodedSequence,
            target,
            argc
        });
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserDiagnostics);
        Assert.Equal(0, binderDiagnostics);
        Assert.Equal(1, compiledMain.ParameterCount);
        Assert.Equal(1, restoredMain.ParameterCount);
        Assert.Equal(new[] { Opcodes.LoadArg, Opcodes.Call, Opcodes.Return }, sequence);
        Assert.Equal(0, slot);
        Assert.Equal(expectedTarget, target);
        Assert.Equal(1, argc);
        return new CallRoute(restored, restoredMain.Name, target, argc);
    }

    private static (byte[] Sequence, int Slot, int TargetIndex, int Argc) Decode(byte[] bytes)
    {
        var sequence = new List<byte>();
        var slots = new List<int>();
        var calls = new List<(int TargetIndex, int Argc)>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            sequence.Add(opcode);
            switch (opcode)
            {
                case Opcodes.LoadArg:
                    Assert.Equal(OperandKind.Int32, format.Operand);
                    slots.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
                    break;
                case Opcodes.Call:
                    Assert.Equal(OperandKind.Int32x2, format.Operand);
                    calls.Add((BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
                    break;
                case Opcodes.Return:
                    Assert.Equal(OperandKind.None, format.Operand);
                    break;
            }
            offset += width;
        }
        Assert.Equal(bytes.Length, offset);
        var (targetIndex, argc) = Assert.Single(calls);
        return (sequence.ToArray(), Assert.Single(slots), targetIndex, argc);
    }

    private void Run(CallRoute route, string row, double value, string expectedInput, string expected)
    {
        var input = TsValue.FromFloat64(value);
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, [input]);
        string? inputBits = input is TsFloat64Value inputFloat
            ? (double.IsNaN(inputFloat.Value) ? "NaN" : Hex(inputFloat.Value))
            : null;
        string? observed = result is TsFloat64Value resultFloat ? Hex(resultFloat.Value) : null;
        Write(new
        {
            kind = "execution",
            row,
            target = route.Target,
            argc = route.Argc,
            inputWrapper = input.GetType().Name,
            inputTag = input.ValueType.ToString(),
            inputBits,
            returnedNormally = true,
            resultWrapper = result?.GetType().Name,
            resultTag = result?.ValueType.ToString(),
            expected,
            observed
        });
        Assert.IsType<TsFloat64Value>(input);
        Assert.Equal(TsValueType.Float64, input.ValueType);
        Assert.Equal(expectedInput, inputBits);
        Assert.Equal(TsValueType.Float64, Assert.IsType<TsFloat64Value>(result).ValueType);
        Assert.Equal(expected, observed);
    }

    private static string Hex(double value) =>
        BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);

    private void Write(object observation) =>
        output.WriteLine("C224_MATH_CLZ32 " + JsonSerializer.Serialize(observation));
}
