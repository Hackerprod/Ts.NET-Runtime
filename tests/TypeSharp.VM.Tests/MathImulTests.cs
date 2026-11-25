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

public sealed class MathImulTests(ITestOutputHelper output)
{
    private const string WitnessSource = "function main(x: number, y: number): number { return Math.imul(x, y); }";
    private const string WitnessFile = "c225-math-imul.ts";
    private const string WitnessMethod = "imul";

    [Fact]
    public void Imul_ReturnsSignedLow32BitsOfNumericProduct()
    {
        string[] mathMembers = ReadMathMembers();
        Control("function main(x: number, y: number): number { return Math.pow(x, y); }",
            "c225-math-pow.ts", "pow", ["x", "y"], [new(2d, "4000000000000000"), new(3d, "4008000000000000")],
            "4020000000000000");
        Control("function main(x: number): number { return Math.clz32(x); }",
            "c225-math-clz32.ts", "clz32", ["x"], [new(4294967312d, "41F0000001000000")], "403B000000000000");

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
            new("Error", "None", 0, WitnessFile, 1, 54, 53, "No member 'imul' on type 'Math'"),
            new("Error", "TS2010", 2010, WitnessFile, 1, 47, 46,
                "Cannot return 'void' from function returning 'number'")
        ];
        bool absent = diagnostics.SequenceEqual(expectedAbsentDiagnostics) && tableEntryCount == 0
            && templateMemberCount == 0 && templateSignature is null;
        bool present = diagnostics.Length == 0 && tableEntryCount == 1 && templateMemberCount == 1
            && templateSignature == "imul(x: number, y: number): number;";
        Assert.True(absent || present, "Unexpected joint binder, VM builtin, and template availability state");
        Assert.True(present, "C225_MATH_IMUL_SUPPORTED");

        var route = Compile(WitnessSource, WitnessFile, WitnessMethod, ["x", "y"], bound,
            lexerErrors, parserDiagnostics, diagnostics.Length, tableEntryCount, templateMemberCount, templateSignature!);
        (string Name, Input X, Input Y, string Expected)[] cases =
        [
            ("ordinary", new(7d, "401C000000000000"), new(9d, "4022000000000000"), "404F800000000000"),
            ("negative", new(-3d, "C008000000000000"), new(5d, "4014000000000000"), "C02E000000000000"),
            ("signed-high-bit", new(1073741824d, "41D0000000000000"), new(2d, "4000000000000000"), "C1E0000000000000"),
            ("past-signed-high-bit", new(2147483647d, "41DFFFFFFFC00000"), new(2d, "4000000000000000"), "C000000000000000"),
            ("both-uint-max", new(4294967295d, "41EFFFFFFFE00000"), new(4294967295d, "41EFFFFFFFE00000"), "3FF0000000000000"),
            ("fractional-operands", new(3.9d, "400F333333333333"), new(-2.9d, "C007333333333333"), "C018000000000000"),
            ("independent-wrap", new(4294967297d, "41F0000000100000"), new(-4294967294d, "C1EFFFFFFFC00000"), "4000000000000000"),
            ("high-bit-negative-one", new(2147483648d, "41E0000000000000"), new(-1d, "BFF0000000000000"), "C1E0000000000000"),
            ("negative-subunit", new(-0.75d, "BFE8000000000000"), new(-7d, "C01C000000000000"), "0000000000000000"),
            ("unsigned-modulus", new(4294967296d, "41F0000000000000"), new(17d, "4031000000000000"), "0000000000000000"),
            ("negative-zero", new(BitConverter.Int64BitsToDouble(long.MinValue), "8000000000000000"),
                new(-5d, "C014000000000000"), "0000000000000000"),
            ("nan-left", new(double.NaN, "NaN"), new(5d, "4014000000000000"), "0000000000000000"),
            ("positive-infinity-right", new(5d, "4014000000000000"),
                new(double.PositiveInfinity, "7FF0000000000000"), "0000000000000000"),
            ("negative-infinity-left", new(double.NegativeInfinity, "FFF0000000000000"),
                new(5d, "4014000000000000"), "0000000000000000")
        ];
        foreach (var row in cases)
            Run(route, row.Name, [row.X, row.Y], row.Expected);

        void Control(string source, string file, string method, string[] formalNames, Input[] inputs, string expected)
        {
            var (control, lexical, parser, binder) = Bind(source, file);
            var (table, count, signature) = Inspect(mathMembers, method);
            Assert.Equal(1, table);
            Assert.Equal(1, count);
            string parameters = string.Join(", ", formalNames.Select(name => $"{name}: number"));
            Assert.Equal($"{method}({parameters}): number;", signature);
            var controlRoute = Compile(source, file, method, formalNames, control, lexical, parser, binder.Length,
                table, count, signature!);
            Run(controlRoute, method, inputs, expected);
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
    private readonly record struct Input(double Value, string Bits);

    private CallRoute Compile(string source, string file, string methodName, string[] formalNames, BoundSourceFile bound,
        int lexerErrors, int parserDiagnostics, int binderDiagnostics, int tableEntryCount,
        int templateMemberCount, string templateSignature)
    {
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserDiagnostics);
        Assert.Equal(0, binderDiagnostics);
        int arity = formalNames.Length;
        string expectedTarget = $"Math::{methodName}";
        int[] expectedSlots = Enumerable.Range(0, arity).ToArray();
        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(TsType.Number, function.Symbol.Type);
        var parameters = function.Symbol.Parameters.ToArray();
        Assert.Equal(formalNames, parameters.Select(parameter => parameter.Name).ToArray());
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
        var arguments = boundCall.Arguments.ToArray();
        Assert.Equal(arity, arguments.Length);
        var boundOperands = expectedSlots.Select(slot =>
        {
            var parameter = parameters[slot];
            Assert.Same(TsType.Number, parameter.Type);
            var argument = Assert.IsType<BoundVariableExpression>(arguments[slot]);
            var argumentSymbol = Assert.IsType<ParameterSymbol>(argument.Symbol);
            int argumentSlot = Array.FindIndex(parameters, candidate => ReferenceEquals(candidate, argumentSymbol));
            Assert.Same(parameter, argumentSymbol);
            Assert.Equal(slot, argumentSlot);
            Assert.Same(TsType.Number, argument.Type);
            return new
            {
                formalSlot = slot, formal = parameter.Name, formalType = parameter.Type.ToString(),
                argumentSlot, argument = argumentSymbol.Name, argumentType = argument.Type.ToString(),
                sameSymbol = ReferenceEquals(parameter, argumentSymbol)
            };
        }).ToArray();

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, candidate => candidate.Name == function.Symbol.Name);
        Assert.Equal(formalNames, irMain.Parameters.Select(parameter => parameter.Name).ToArray());
        var instructions = Assert.Single(irMain.Blocks).Instructions.ToArray();
        Assert.Equal(arity + 2, instructions.Length);
        int[] irSlots = expectedSlots.Select(slot =>
        {
            var load = instructions[slot];
            Assert.Equal(Opcode.LoadArg, load.Opcode);
            Assert.Equal(slot, load.Operand0);
            return load.Operand0;
        }).ToArray();
        var call = instructions[arity];
        Assert.Equal(Opcode.Call, call.Opcode);
        string irTarget = Assert.IsType<string>(call.OperandObject);
        Assert.Equal(expectedTarget, irTarget);
        Assert.Equal(arity, call.Operand1);
        Assert.Equal(Opcode.Return, instructions[arity + 1].Opcode);

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, candidate => candidate.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, candidate => candidate.Name == irMain.Name);
        Assert.Equal(arity, compiledMain.ParameterCount);
        Assert.Equal(arity, restoredMain.ParameterCount);
        bool sameInstructionBytes = compiledMain.Instructions.SequenceEqual(restoredMain.Instructions);
        bool sameStringTable = compiledMain.StringConstants.SequenceEqual(restoredMain.StringConstants, StringComparer.Ordinal);
        Assert.True(sameInstructionBytes);
        Assert.True(sameStringTable);
        var (decodedSequence, decodedSlots, targetIndex, target, argc, consumedBytes) =
            Decode(restoredMain.Instructions, restoredMain.StringConstants);
        Assert.Equal(Enumerable.Repeat("LoadArg", arity).Concat(["Call", "Return"]).ToArray(), decodedSequence);
        Assert.Equal(expectedSlots, decodedSlots);
        Assert.Equal(expectedTarget, target);
        Assert.Equal(arity, argc);

        Write(new
        {
            kind = "route",
            source,
            file,
            lexerErrors,
            parserDiagnostics,
            binderDiagnostics,
            tableEntryCount,
            templateMemberCount,
            templateSignature,
            main = restoredMain.Name,
            functionResultType = function.Symbol.Type.ToString(),
            boundOperands,
            receiverKind = math.GetType().Name,
            receiver = math.Name,
            methodKind = method.GetType().Name,
            method = method.Name,
            owner = method.DeclaringClassName,
            methodResultType = method.Type.ToString(),
            callResultType = boundCall.Type.ToString(),
            dynamicSignature = method.HasDynamicSignature,
            methodParameterCount = method.Parameters.Count(),
            descriptorResultType = descriptor.ReturnType.ToString(),
            descriptorParameterCount = descriptor.Parameters.Count(),
            irSequence = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
            irSlots,
            irTarget,
            irArgc = call.Operand1,
            compiledParameterCount = compiledMain.ParameterCount,
            restoredParameterCount = restoredMain.ParameterCount,
            sameInstructionBytes,
            sameStringTable,
            decodedSequence,
            decodedSlots,
            targetIndex,
            stringTableLength = restoredMain.StringConstants.Length,
            target,
            argc,
            consumedBytes,
            instructionByteLength = restoredMain.Instructions.Length
        });
        return new CallRoute(restored, restoredMain.Name, target, argc);
    }

    private static (string[] Sequence, int[] Slots, int TargetIndex, string Target, int Argc, int ConsumedBytes)
        Decode(byte[] bytes, string[] strings)
    {
        var sequence = new List<string>();
        var slots = new List<int>();
        var calls = new List<(int TargetIndex, string Target, int Argc)>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            switch (opcode)
            {
                case Opcodes.LoadArg:
                    Assert.Equal(OperandKind.Int32, format.Operand);
                    slots.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
                    sequence.Add("LoadArg");
                    break;
                case Opcodes.Call:
                    Assert.Equal(OperandKind.Int32x2, format.Operand);
                    int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
                    Assert.InRange(index, 0, strings.Length - 1);
                    calls.Add((index, strings[index], BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
                    sequence.Add("Call");
                    break;
                case Opcodes.Return:
                    Assert.Equal(OperandKind.None, format.Operand);
                    sequence.Add("Return");
                    break;
                default:
                    Assert.True(false, $"Unexpected opcode at {offset}: {opcode}");
                    break;
            }
            offset += width;
        }
        Assert.Equal(bytes.Length, offset);
        var (targetIndex, target, argc) = Assert.Single(calls);
        return (sequence.ToArray(), slots.ToArray(), targetIndex, target, argc, offset);
    }

    private void Run(CallRoute route, string row, Input[] inputs, string expected)
    {
        Assert.Equal(route.Argc, inputs.Length);
        var values = inputs.Select(input => TsValue.FromFloat64(input.Value)).ToArray();
        var orderedInputs = values.Select((input, slot) =>
        {
            var number = Assert.IsType<TsFloat64Value>(input);
            Assert.Equal(TsValueType.Float64, input.ValueType);
            string bits = double.IsNaN(number.Value) ? "NaN" : Hex(number.Value);
            Assert.Equal(inputs[slot].Bits, bits);
            return new { slot, wrapper = input.GetType().Name, tag = input.ValueType.ToString(), bits };
        }).ToArray();
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, values);
        var resultFloat = Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(TsValueType.Float64, resultFloat.ValueType);
        string observed = Hex(resultFloat.Value);
        Write(new
        {
            kind = "execution",
            row,
            target = route.Target,
            argc = route.Argc,
            inputs = orderedInputs,
            returnedNormally = true,
            resultWrapper = resultFloat.GetType().Name,
            resultTag = resultFloat.ValueType.ToString(),
            expected,
            observed
        });
        Assert.Equal(expected, observed);
    }

    private static string Hex(double value) =>
        BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object observation) =>
        output.WriteLine("C225_MATH_IMUL " + JsonSerializer.Serialize(observation));
}
