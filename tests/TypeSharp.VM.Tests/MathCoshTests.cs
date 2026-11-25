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

public sealed class MathCoshTests(ITestOutputHelper output)
{
    private const string WitnessSource = "function main(x: number): number { return Math.cosh(x); }";
    private const string WitnessFile = "c228-math-cosh.ts";
    private const string WitnessMethod = "cosh";
    private const ulong Sign = 0x8000000000000000UL;
    private const ulong MaxUlps = 4;

    [Fact]
    public void Cosh_ReturnsHyperbolicCosineOfNumber()
    {
        string[] mathMembers = ReadMathMembers();
        double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        Control("function main(x: number): number { return Math.sinh(x); }",
            "c228-math-sinh.ts", "sinh", negativeZero, negativeZero);
        Control("function main(x: number): number { return Math.tanh(x); }",
            "c228-math-tanh.ts", "tanh", 0d, 0d);

        var (bound, lexerErrors, parserDiagnostics, diagnostics) = Bind(WitnessSource, WitnessFile);
        var (tableEntryCount, templateMemberCount, templateSignature) = Inspect(mathMembers, WitnessMethod);
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserDiagnostics);
        DiagnosticProfile[] expectedAbsentDiagnostics =
        [
            new("Error", "None", 0, WitnessFile, 1, 43, 42, "No member 'cosh' on type 'Math'"),
            new("Error", "TS2010", 2010, WitnessFile, 1, 36, 35,
                "Cannot return 'void' from function returning 'number'")
        ];
        bool absent = diagnostics.SequenceEqual(expectedAbsentDiagnostics) && tableEntryCount == 0
            && templateMemberCount == 0 && templateSignature is null;
        bool present = diagnostics.Length == 0 && tableEntryCount == 1 && templateMemberCount == 1
            && templateSignature == "cosh(x: number): number;";
        Assert.True(absent || present, "Unexpected joint binder, VM builtin, and template availability state");
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
            templateMember = WitnessMethod,
            templateSignature,
            profile = present ? "present" : "absent"
        });
        Assert.True(present, "C228_MATH_COSH_SUPPORTED");

        var route = Compile(WitnessSource, WitnessFile, WitnessMethod, bound,
            lexerErrors, parserDiagnostics, diagnostics.Length, tableEntryCount, templateMemberCount, templateSignature!);
        (string Name, double Input, double Expected, Comparison Check)[] cases =
        [
            ("positive-zero", 0d, 1d, Comparison.Exact),
            ("negative-zero", negativeZero, 1d, Comparison.Exact),
            // Independently round (exp(x) + exp(-x)) / 2 from 110-digit decimal to binary64.
            ("positive-one", 1d, FromBits(0x3FF8B07551D9F550UL), Comparison.Ulp),
            ("negative-one", -1d, FromBits(0x3FF8B07551D9F550UL), Comparison.Ulp),
            ("positive-infinity", double.PositiveInfinity, double.PositiveInfinity, Comparison.Exact),
            ("negative-infinity", double.NegativeInfinity, double.PositiveInfinity, Comparison.Exact),
            ("nan", double.NaN, double.NaN, Comparison.NaN)
        ];
        foreach (var row in cases)
            Run(route, row.Name, row.Input, row.Expected, row.Check);

        void Control(string source, string file, string method, double input, double expected)
        {
            var (control, lexical, parser, binder) = Bind(source, file);
            var (table, count, signature) = Inspect(mathMembers, method);
            Assert.Equal(1, table);
            Assert.Equal(1, count);
            Assert.Equal($"{method}(x: number): number;", signature);
            var controlRoute = Compile(source, file, method, control, lexical, parser, binder.Length,
                table, count, signature!);
            Run(controlRoute, method, input, expected, Comparison.Exact);
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
    private enum Comparison { Exact, Ulp, NaN }

    private CallRoute Compile(string source, string file, string methodName, BoundSourceFile bound,
        int lexerErrors, int parserDiagnostics, int binderDiagnostics, int tableEntryCount,
        int templateMemberCount, string templateSignature)
    {
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserDiagnostics);
        Assert.Equal(0, binderDiagnostics);
        const int arity = 1;
        string[] formalNames = ["x"];
        string expectedTarget = $"Math::{methodName}";
        int[] expectedSlots = [0];
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
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
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
        var compiledMain = Assert.Single(compiled.Functions);
        var restoredMain = Assert.Single(restored.Functions);
        Assert.Equal(irMain.Name, compiledMain.Name);
        Assert.Equal(irMain.Name, restoredMain.Name);
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
        Assert.Equal(0, targetIndex);
        Assert.Equal(expectedTarget, Assert.Single(restoredMain.StringConstants));
        Assert.Equal(expectedTarget, target);
        Assert.Equal(arity, argc);
        Assert.Equal(15, consumedBytes);
        Assert.Equal(15, restoredMain.Instructions.Length);

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
            templateMember = methodName,
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
                    Assert.Equal(4, format.OperandBytes);
                    slots.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
                    sequence.Add("LoadArg");
                    break;
                case Opcodes.Call:
                    Assert.Equal(OperandKind.Int32x2, format.Operand);
                    Assert.Equal(8, format.OperandBytes);
                    int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
                    Assert.InRange(index, 0, strings.Length - 1);
                    calls.Add((index, strings[index], BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
                    sequence.Add("Call");
                    break;
                case Opcodes.Return:
                    Assert.Equal(OperandKind.None, format.Operand);
                    Assert.Equal(0, format.OperandBytes);
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

    private void Run(CallRoute route, string row, double input, double expected, Comparison check)
    {
        Assert.Equal(1, route.Argc);
        var argument = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(input));
        Assert.Equal(TsValueType.Float64, argument.ValueType);
        ulong inputBits = Bits(argument.Value);
        if (double.IsNaN(input))
            Assert.True(double.IsNaN(argument.Value));
        else
            Assert.Equal(Bits(input), inputBits);

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, [argument]);
        var resultFloat = Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(TsValueType.Float64, resultFloat.ValueType);
        ulong actualBits = Bits(resultFloat.Value);
        ulong expectedBits = Bits(expected);
        bool finite = double.IsFinite(resultFloat.Value);
        bool sameSign = (actualBits & Sign) == (expectedBits & Sign);
        ulong? distance = null;
        if (check == Comparison.Ulp && finite && sameSign)
        {
            distance = actualBits >= expectedBits ? actualBits - expectedBits : expectedBits - actualBits;
        }

        Write(new
        {
            kind = "execution",
            row,
            target = route.Target,
            argc = route.Argc,
            inputWrapper = argument.GetType().Name,
            inputTag = argument.ValueType.ToString(),
            inputBits = Hex(inputBits),
            returnedNormally = true,
            resultWrapper = resultFloat.GetType().Name,
            resultTag = resultFloat.ValueType.ToString(),
            observed = Hex(actualBits),
            expected = check == Comparison.NaN ? "NaN" : Hex(expectedBits),
            comparison = check.ToString(),
            toleranceUlps = check == Comparison.Ulp ? MaxUlps : (ulong?)null,
            distanceUlps = distance,
            resultIsFinite = finite,
            resultIsNaN = double.IsNaN(resultFloat.Value)
        });
        switch (check)
        {
            case Comparison.Exact:
                Assert.Equal(Hex(expectedBits), Hex(actualBits));
                break;
            case Comparison.Ulp:
                Assert.True(finite && resultFloat.Value > 0d, $"{row}: expected positive finite result");
                Assert.True(sameSign, $"{row}: expected sign of {Hex(expectedBits)}, got {Hex(actualBits)}");
                Assert.NotNull(distance);
                Assert.InRange(distance.Value, 0UL, MaxUlps);
                break;
            case Comparison.NaN:
                Assert.True(double.IsNaN(resultFloat.Value), $"{row}: expected NaN, got {Hex(actualBits)}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(check));
        }
    }

    private static double FromBits(ulong bits) => BitConverter.Int64BitsToDouble(unchecked((long)bits));
    private static ulong Bits(double value) => unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
    private static string Hex(ulong bits) => bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object observation) =>
        output.WriteLine("C228_MATH_COSH " + JsonSerializer.Serialize(observation));
}
