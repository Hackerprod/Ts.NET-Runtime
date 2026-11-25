using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
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

public sealed class NumberIsIntegerBindingTests(ITestOutputHelper output)
{
    private const string WitnessSource = "function main(input: any): bool { return Number.isInteger(input); }";
    private const string WitnessFile = "c230-number-is-integer.ts";
    private const string WitnessMethod = "isInteger";

    [Fact]
    public void NumberIsInteger_BindsDefaultDirectCallWithoutCoercion()
    {
        string[] members = ReadNumberMembers();
        var safe = Control("function main(input: any): bool { return Number.isSafeInteger(input); }",
            "c230-number-is-safe-integer-control.ts", "isSafeInteger");
        Run(safe, F64("positive-limit", 9007199254740991d, "433FFFFFFFFFFFFF", true));
        Run(safe, F64("negative-limit", -9007199254740991d, "C33FFFFFFFFFFFFF", true));
        Run(safe, F64("positive-outside-safe-limit", 9007199254740992d, "4340000000000000", false));
        Run(safe, F64("negative-outside-safe-limit", -9007199254740992d, "C340000000000000", false));
        Run(safe, new("numeric-string", TsValue.FromString("42"), typeof(TsStringValue), TsValueType.String, "42", false));

        CheckShadow();

        var (bound, lexerErrors, parserDiagnostics, diagnostics) = Bind(WitnessSource, WitnessFile);
        var availability = Inspect(members, WitnessMethod);
        DiagnosticProfile[] expectedAbsentDiagnostics =
        [
            new("Error", "None", 0, WitnessFile, 1, 42, 41, "No member 'isInteger' on type '() => number'"),
            new("Error", "TS2010", 2010, WitnessFile, 1, 35, 34,
                "Cannot return 'void' from function returning 'bool'")
        ];
        var (tableEntryCount, templateMemberCount, templateSignature) = availability;
        bool absent = diagnostics.SequenceEqual(expectedAbsentDiagnostics) && tableEntryCount == 1
            && templateMemberCount == 0 && templateSignature is null;
        bool present = diagnostics.Length == 0 && tableEntryCount == 1 && templateMemberCount == 1
            && templateSignature == Signature(WitnessMethod);
        Assert.True(absent ^ present, "Unexpected joint binder, VM builtin, and template availability state");
        Write(new
        {
            kind = "availability",
            source = WitnessSource,
            file = WitnessFile,
            lexerErrors,
            parserDiagnostics,
            binderCount = diagnostics.Length,
            diagnostics,
            tableEntryCount,
            templateMemberCount,
            templateMember = WitnessMethod,
            templateSignature,
            profile = present ? "present" : "absent"
        });
        Assert.True(present, "C230_NUMBER_IS_INTEGER_SUPPORTED");

        var route = Compile(WitnessSource, WitnessFile, WitnessMethod, bound, lexerErrors, parserDiagnostics,
            diagnostics.Length, tableEntryCount, templateMemberCount, templateSignature!);
        InputRow[] rows =
        [
            new("int32", TsValue.FromInt32(42), typeof(TsInt32Value), TsValueType.Int32, "42", true),
            new("float32-integral", TsValue.FromFloat32(42f), typeof(TsFloat32Value), TsValueType.Float32, "42280000", true),
            new("float32-fraction", TsValue.FromFloat32(3.5f), typeof(TsFloat32Value), TsValueType.Float32, "40600000", false),
            F64("negative-zero", BitConverter.Int64BitsToDouble(long.MinValue), "8000000000000000", true),
            F64("positive-outside-safe-limit", 9007199254740992d, "4340000000000000", true),
            F64("negative-outside-safe-limit", -9007199254740992d, "C340000000000000", true),
            F64("fraction", 3.5d, "400C000000000000", false),
            F64("positive-infinity", double.PositiveInfinity, "7FF0000000000000", false),
            F64("nan", BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000UL)), "FFF8000000000000", false),
            new("numeric-string", TsValue.FromString("42"), typeof(TsStringValue), TsValueType.String, "42", false),
            new("boolean", TsValue.FromBool(true), typeof(TsBoolValue), TsValueType.Bool, "true", false),
            new("null", TsValue.Null, typeof(TsNull), TsValueType.Null, null, false),
            new("void", TsValue.Void, typeof(TsVoid), TsValueType.Void, null, false),
            new("int64", TsValue.FromInt64(42L), typeof(TsInt64Value), TsValueType.Int64, "42", false),
            new("uint64", TsValue.FromUInt64(42UL), typeof(TsUInt64Value), TsValueType.UInt64, "42", false),
            new("bigint", TsValue.FromBigInt(new BigInteger(42)), typeof(TsBigIntValue), TsValueType.BigInt, "42", false),
            new("decimal-legacy-fallback", TsValue.FromDecimal(42m), typeof(TsDecimalValue), TsValueType.Decimal, "42", false)
        ];
        foreach (var row in rows)
            Run(route, row);

        CallRoute Control(string source, string file, string method)
        {
            var (control, lexical, parser, binder) = Bind(source, file);
            var (table, count, signature) = Inspect(members, method);
            Assert.Equal(1, table);
            Assert.Equal(1, count);
            Assert.Equal(Signature(method), signature);
            return Compile(source, file, method, control, lexical, parser, binder.Length, table, count, signature!);
        }
    }

    private void CheckShadow()
    {
        const string source = "function shadow(Number: any, input: any): any { return Number.isInteger(input); }";
        const string file = "c230-number-shadow.ts";
        var (bound, lexerErrors, parserDiagnostics, diagnostics) = Bind(source, file);
        Assert.Empty(diagnostics);
        var (declaration, call) = GetReturnedCall(bound, "shadow");
        Assert.Same(TsType.Any, declaration.Symbol.Type);
        var parameters = declaration.Symbol.Parameters.ToArray();
        Assert.Equal(new[] { "Number", "input" }, parameters.Select(parameter => parameter.Name).ToArray());
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        var parameter = Assert.IsType<ParameterSymbol>(receiver.Symbol);
        Assert.Same(parameters[0], parameter);
        Assert.Same(TsType.Any, parameter.Type);
        Assert.Same(TsType.Any, receiver.Type);
        var property = Assert.IsType<PropertySymbol>(access.Member);
        Assert.Equal(WitnessMethod, property.Name);
        Assert.Same(TsType.Any, property.Type);
        Assert.Same(TsType.Any, access.Type);
        Assert.Same(TsType.Any, call.Type);
        var argument = AssertArgument(call, parameters, 1);
        Write(new
        {
            kind = "shadow",
            source,
            file,
            lexerErrors,
            parserDiagnostics,
            binderCount = diagnostics.Length,
            diagnostics,
            function = declaration.Symbol.Name,
            functionResultType = declaration.Symbol.Type.ToString(),
            receiverKind = parameter.GetType().Name,
            receiver = parameter.Name,
            receiverType = receiver.Type.ToString(),
            receiverSlot = Array.FindIndex(parameters, candidate => ReferenceEquals(candidate, parameter)),
            receiverSameSymbol = ReferenceEquals(parameters[0], parameter),
            memberKind = property.GetType().Name,
            member = property.Name,
            memberType = property.Type.ToString(),
            accessType = access.Type.ToString(),
            callResultType = call.Type.ToString(),
            boundOperands = new[] { argument }
        });
    }

    private static (BoundFunctionDeclaration Declaration, BoundCallExpression Call) GetReturnedCall(
        BoundSourceFile bound, string name)
    {
        var declaration = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal(name, declaration.Symbol.Name);
        var body = Assert.IsType<BoundBlockStatement>(declaration.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        return (declaration, Assert.IsType<BoundCallExpression>(ret.Value));
    }

    private sealed record ArgumentEvidence(
        int formalSlot, string formal, string formalType, int argumentSlot, string argument, string argumentType,
        bool sameSymbol);

    private static ArgumentEvidence AssertArgument(BoundCallExpression call, ParameterSymbol[] parameters, int slot)
    {
        var parameter = parameters[slot];
        Assert.Equal("input", parameter.Name);
        Assert.Same(TsType.Any, parameter.Type);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(call.Arguments));
        var argumentSymbol = Assert.IsType<ParameterSymbol>(argument.Symbol);
        Assert.Same(parameter, argumentSymbol);
        Assert.Same(TsType.Any, argument.Type);
        int argumentSlot = Array.FindIndex(parameters, candidate => ReferenceEquals(candidate, argumentSymbol));
        Assert.Equal(slot, argumentSlot);
        return new(slot, parameter.Name, parameter.Type.ToString(), argumentSlot,
            argumentSymbol.Name, argument.Type.ToString(), ReferenceEquals(parameter, argumentSymbol));
    }

    private static string Signature(string method) => $"{method}(value: unknown): boolean;";

    private static string[] ReadNumberMembers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string path = Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts");
        string declaration = File.ReadAllText(path);
        var number = Assert.Single(Regex.Matches(declaration, @"(?m)^interface[ \t]+NumberConstructor\b[^\S\n]*\{([^{}]*)\}"));
        return number.Groups[1].Value.Split(';');
    }

    private static (int TableEntryCount, int TemplateMemberCount, string? TemplateSignature) Inspect(
        string[] members, string method)
    {
        string target = $"Number::{method}";
        int tableEntryCount = Builtins.Names.Count(name => string.Equals(name, target, StringComparison.Ordinal));
        var memberIndices = Enumerable.Range(0, members.Length)
            .Where(index => Regex.IsMatch(members[index].TrimStart(), "^" + Regex.Escape(method) + @"\b"))
            .ToArray();
        foreach (int index in memberIndices)
            Assert.True(index < members.Length - 1, $"Number.{method} must end in a semicolon");
        string? signature = memberIndices.Length == 1 ? members[memberIndices[0]].Trim() + ";" : null;
        return (tableEntryCount, memberIndices.Length, signature);
    }

    private sealed record DiagnosticProfile(
        string severity, string code, int codeNumber, string source, int line, int column, int offset, string message);

    private static (BoundSourceFile Bound, int LexerErrors, int ParserDiagnostics, DiagnosticProfile[] Diagnostics)
        Bind(string source, string file)
    {
        var tokens = new Lexer(source, file).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var parsed = parser.Parse();
        Assert.Empty(parser.Diagnostics);
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
        int lexerErrors, int parserDiagnostics, int binderDiagnostics, int tableEntryCount,
        int templateMemberCount, string templateSignature)
    {
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserDiagnostics);
        Assert.Equal(0, binderDiagnostics);
        const int arity = 1;
        string[] formalNames = ["input"];
        string expectedTarget = $"Number::{methodName}";
        int[] expectedSlots = [0];
        var (function, boundCall) = GetReturnedCall(bound, "main");
        Assert.Same(TsType.Bool, function.Symbol.Type);
        var parameters = function.Symbol.Parameters.ToArray();
        Assert.Single(parameters);
        Assert.Same(TsType.Bool, boundCall.Type);
        var access = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        var number = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Number", number.Name);
        Assert.Same(number.ClassType, receiver.Type);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal(number.Name, method.DeclaringClassName);
        Assert.Equal(methodName, method.Name);
        var descriptor = number.ClassType.Methods[methodName];
        Assert.Equal(methodName, descriptor.Name);
        Assert.Same(TsType.Bool, descriptor.ReturnType);
        Assert.Empty(descriptor.Parameters);
        Assert.Same(TsType.Bool, method.Type);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        var boundOperands = new[] { AssertArgument(boundCall, parameters, 0) };

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
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        BytecodeVerifier.Verify(restored);
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
            receiverKind = number.GetType().Name,
            receiver = number.Name,
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

    private sealed record InputRow(
        string Label, TsValue Input, Type Wrapper, TsValueType Tag, string? Payload, bool Expected);

    private static InputRow F64(string label, double input, string bits, bool expected) =>
        new(label, TsValue.FromFloat64(input), typeof(TsFloat64Value), TsValueType.Float64, bits, expected);

    private static string Float64Bits(double value) =>
        unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("X16", CultureInfo.InvariantCulture);

    private static string? DescribePayload(TsValue input) => input switch
    {
        TsFloat32Value number => unchecked((uint)BitConverter.SingleToInt32Bits(number.Value))
            .ToString("X8", CultureInfo.InvariantCulture),
        TsFloat64Value number => Float64Bits(number.Value),
        TsInt32Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsUInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsBigIntValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsDecimalValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsStringValue value => value.Value,
        TsBoolValue value => value.Value ? "true" : "false",
        TsNull or TsVoid => null,
        _ => throw new InvalidOperationException("Unexpected input wrapper")
    };

    private void Run(CallRoute route, InputRow row)
    {
        Assert.Equal(1, route.Argc);
        Assert.Equal(row.Wrapper, row.Input.GetType());
        Assert.Equal(row.Tag, row.Input.ValueType);
        string? payload = DescribePayload(row.Input);
        Assert.Equal(row.Payload, payload);
        if (row.Input is TsNull or TsVoid)
            Assert.Null(row.Input.RawValue);

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, [row.Input]);
        var boolean = Assert.IsType<TsBoolValue>(result);
        Assert.Equal(TsValueType.Bool, boolean.ValueType);
        Assert.Equal(row.Expected, boolean.Value);
        Write(new
        {
            kind = "execution",
            label = row.Label,
            target = route.Target,
            argc = route.Argc,
            inputWrapper = row.Input.GetType().Name,
            inputTag = row.Input.ValueType.ToString(),
            inputPayload = payload,
            returnedNormally = true,
            resultWrapper = boolean.GetType().Name,
            resultTag = boolean.ValueType.ToString(),
            expected = row.Expected,
            observed = boolean.Value
        });
    }

    private void Write(object observation) =>
        output.WriteLine("C230_NUMBER_IS_INTEGER " + JsonSerializer.Serialize(observation));
}
