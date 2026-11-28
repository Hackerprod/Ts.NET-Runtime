using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathCosNullTests(ITestOutputHelper output)
{
    private const string One = "3FF0000000000000";
    private const string NaN = "NaN";
    private const long OneBits = 0x3FF0000000000000L;

    [Fact]
    public void Cos_ExplicitNullReturnsOne()
    {
        var cosValue = BuildRoute("cos-value", "c251-math-cos-null.ts",
            "function main(value: any): number { return Math.cos(value); }", "cos", true);
        var cosOmitted = BuildRoute("cos-omitted", "c251-math-cos-omitted.ts",
            "function main(): number { return Math.cos(); }", "cos", false);
        var sinKin = BuildRoute("sin-kin", "c251-math-sin-kin.ts",
            "function main(value: any): number { return Math.sin(value); }", "sin", true);

        var rows = new (string Row, Executable Route, TsValue[] Args, string InputType, object? Input)[]
        {
            ("explicit-null", cosValue, [TsValue.Null], "Null", null),
            ("explicit-undefined", cosValue, [TsValue.Void], "Void", "undefined"),
            ("omitted", cosOmitted, [], "Omitted", null),
            ("positive-zero", cosValue, [FromBits(0L)], "Float64", "0000000000000000"),
            ("negative-zero", cosValue, [FromBits(long.MinValue)], "Float64", "8000000000000000"),
            ("finite-one", cosValue, [FromBits(OneBits)], "Float64", One),
            ("kin-null", sinKin, [TsValue.Null], "Null", null)
        };

        var observations = new (double Value, long Bits, string Observed)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var (row, route, args, expectedType, expectedInput) = rows[i];
            Assert.Equal(route.Decoded.argc, args.Length);
            string inputType;
            object? input;
            if (args.Length == 0)
            {
                Assert.Equal(0, route.ParameterCount);
                inputType = "Omitted";
                input = null;
            }
            else
            {
                Assert.Equal(1, route.ParameterCount);
                var argument = Assert.Single(args);
                inputType = argument.ValueType.ToString();
                switch (argument)
                {
                    case TsNull suppliedNull:
                        Assert.Same(TsValue.Null, suppliedNull);
                        Assert.Equal(TsValueType.Null, suppliedNull.ValueType);
                        Assert.Null(suppliedNull.RawValue);
                        input = suppliedNull.RawValue;
                        break;
                    case TsVoid suppliedVoid:
                        Assert.Same(TsValue.Void, suppliedVoid);
                        Assert.Equal(TsValueType.Void, suppliedVoid.ValueType);
                        Assert.Null(suppliedVoid.RawValue);
                        input = "undefined";
                        break;
                    default:
                        var number = Assert.IsType<TsFloat64Value>(argument);
                        Assert.Equal(TsValueType.Float64, number.ValueType);
                        input = Hex(BitConverter.DoubleToInt64Bits(number.Value));
                        break;
                }
            }
            Assert.Equal(expectedType, inputType);
            Assert.Equal<object?>(expectedInput, input);
            var returned = new TypeSharp.VM.Interpreter.Interpreter()
                .Execute(route.Module, route.Main, args);
            var result = Assert.IsType<TsFloat64Value>(returned);
            Assert.Equal(TsValueType.Float64, result.ValueType);
            long bits = BitConverter.DoubleToInt64Bits(result.Value);
            string observed = double.IsNaN(result.Value) ? NaN : Hex(bits);
            observations[i] = (result.Value, bits, observed);
            Write(new
            {
                kind = "result",
                row,
                route = route.Name,
                argc = route.Decoded.argc,
                inputType,
                input,
                outputType = result.ValueType.ToString(),
                observed,
                observedBits = Hex(bits)
            });
        }

        foreach (var (value, bits, observed) in observations)
        {
            const long ExponentMask = 0x7FF0000000000000L;
            const long SignificandMask = 0x000FFFFFFFFFFFFFL;
            bool bitsAreNaN = (bits & ExponentMask) == ExponentMask && (bits & SignificandMask) != 0L;
            Assert.Equal(double.IsNaN(value), bitsAreNaN);
            Assert.Equal(bitsAreNaN ? NaN : Hex(bits), observed);
        }

        Assert.Equal(NaN, observations[1].Observed);
        Assert.Equal(NaN, observations[2].Observed);
        Assert.Equal(OneBits, observations[3].Bits);
        Assert.Equal(OneBits, observations[4].Bits);
        Assert.True(double.IsFinite(observations[5].Value));
        Assert.True(Math.Abs(observations[5].Value - 0.5403023058681398) <= 1e-15);
        Assert.Equal(0L, observations[6].Bits);

        bool fixedNull = observations[0].Bits == OneBits;
        Assert.True(fixedNull || observations[0].Observed == NaN,
            "C251_COS_NULL_UNEXPECTED_PROFILE observed " + observations[0].Observed);
        Assert.True(fixedNull, "C251_COS_NULL_RESULT expected explicit null to produce Float64 bits 3FF0000000000000");
    }

    private Executable BuildRoute(string route, string file, string source, string member, bool hasArg)
    {
        string target = "Math::" + member;
        int arity = hasArg ? 1 : 0;
        var tokens = new Lexer(source, file).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var mainToken = Assert.Single(tokens.Where(token => token.Kind == TokenKind.Identifier && token.Text == "main"));
        string lexerFile = mainToken.Location.Source;
        Assert.Equal(file, lexerFile);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse(file);
        Assert.Equal(file, syntax.FileName);
        Assert.Empty(parser.Diagnostics);

        var syntaxFunction = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(syntax.Members));
        Assert.Equal("main", syntaxFunction.Name);
        Assert.False(syntaxFunction.IsAsync);
        Assert.False(syntaxFunction.IsGenerator);
        Assert.False(syntaxFunction.IsOverloadSignature);
        Assert.Empty(syntaxFunction.GenericParameters);
        Assert.Empty(syntaxFunction.Modifiers);
        Assert.Equal(TokenKind.NumberKeyword, Assert.IsType<PrimitiveTypeSyntax>(syntaxFunction.ReturnType).TypeKeyword.Kind);
        if (hasArg)
        {
            var syntaxParameter = Assert.Single(syntaxFunction.Parameters);
            Assert.Equal("value", syntaxParameter.Name);
            Assert.Equal(TokenKind.AnyKeyword, Assert.IsType<PrimitiveTypeSyntax>(syntaxParameter.TypeAnnotation).TypeKeyword.Kind);
            Assert.False(syntaxParameter.TypeWasInferred);
            Assert.False(syntaxParameter.IsRest);
            Assert.Null(syntaxParameter.DefaultValue);
        }
        else
        {
            Assert.Empty(syntaxFunction.Parameters);
        }
        var syntaxBody = Assert.IsType<BlockStatementSyntax>(syntaxFunction.Body);
        var syntaxReturn = Assert.IsType<ReturnStatementSyntax>(Assert.Single(syntaxBody.Statements));
        var syntaxCall = Assert.IsType<CallExpressionSyntax>(syntaxReturn.Value);
        Assert.False(syntaxCall.IsNullConditional);
        Assert.Empty(syntaxCall.TypeArguments);
        var syntaxAccess = Assert.IsType<MemberAccessExpressionSyntax>(syntaxCall.Callee);
        Assert.False(syntaxAccess.IsNullConditional);
        Assert.Equal("Math", Assert.IsType<IdentifierExpressionSyntax>(syntaxAccess.Object).Name);
        Assert.Equal(member, syntaxAccess.MemberName);
        if (hasArg)
            Assert.Equal("value", Assert.IsType<IdentifierExpressionSyntax>(Assert.Single(syntaxCall.Arguments)).Name);
        else
            Assert.Empty(syntaxCall.Arguments);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Equal(file, bound.FileName);
        Assert.Empty(binder.Diagnostics.All);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(TsType.Number, function.Symbol.Type);
        Assert.Equal(arity, function.Symbol.Parameters.Count());
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, call.Type);
        Assert.False(call.IsNullConditional);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(access.IsNullConditional);
        var signature = Assert.IsType<TsFunctionType>(access.Type);
        Assert.Same(TsType.Number, signature.ReturnType);
        Assert.True(signature.HasDynamicSignature);
        Assert.Empty(signature.Parameters);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        var math = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal(member, method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        // Builtin Math members bind through the ClassSymbol namespace path even
        // though their declarations do not set the ordinary method IsStatic flag.
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var declaredMethod = math.ClassType.Methods[method.Name];
        Assert.Equal(method.Name, declaredMethod.Name);
        Assert.False(declaredMethod.IsStatic);
        Assert.Empty(declaredMethod.Parameters);
        Assert.Same(TsType.Number, declaredMethod.ReturnType);
        string boundTarget = $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}";
        Assert.Equal(target, boundTarget);
        if (hasArg)
        {
            var parameter = Assert.IsType<ParameterSymbol>(Assert.Single(function.Symbol.Parameters));
            Assert.Equal("value", parameter.Name);
            Assert.Same(TsType.Any, parameter.Type);
            var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(call.Arguments));
            Assert.Same(parameter, Assert.IsType<ParameterSymbol>(argument.Symbol));
            Assert.Same(TsType.Any, argument.Type);
        }
        else
        {
            Assert.Empty(call.Arguments);
        }

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
        if (hasArg)
            Assert.Equal("value", Assert.Single(irMain.Parameters).Name);
        else
            Assert.Empty(irMain.Parameters);
        var instructions = Assert.Single(irMain.Blocks).Instructions.ToArray();
        var expectedOpcodes = hasArg
            ? new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }
            : new[] { Opcode.Call, Opcode.Return };
        Assert.Equal(expectedOpcodes, instructions.Select(instruction => instruction.Opcode).ToArray());
        if (hasArg)
            Assert.Equal(0, instructions[0].Operand0);
        var irCall = instructions[arity];
        Assert.Equal(target, Assert.IsType<string>(irCall.OperandObject));
        Assert.Equal(arity, irCall.Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        Assert.Equal(0L, stream.Position);
        BytecodeSerializer.Serialize(stream, compiled);
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(stream.Length, stream.Position);
        BytecodeVerifier.Verify(restored);
        var compiledMain = Assert.Single(compiled.Functions);
        var restoredMain = Assert.Single(restored.Functions);
        Assert.Equal(irMain.Name, compiledMain.Name);
        Assert.Equal(irMain.Name, restoredMain.Name);
        Assert.Equal(arity, compiledMain.ParameterCount);
        Assert.Equal(arity, restoredMain.ParameterCount);
        var compiledRoute = Decode(compiledMain.Instructions, compiledMain.StringConstants, target, hasArg);
        var restoredRoute = Decode(restoredMain.Instructions, restoredMain.StringConstants, target, hasArg);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);
        Assert.Equal(compiledRoute, restoredRoute);

        Write(new
        {
            kind = "route",
            route,
            lexerFile,
            parsedFile = syntax.FileName,
            boundFile = bound.FileName,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count(),
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            argumentSlot = restoredRoute.argumentSlot,
            target = restoredRoute.target,
            argc = restoredRoute.argc,
            compiledHex = compiledRoute.hex,
            restoredHex = restoredRoute.hex
        });
        return new Executable(route, restored, restoredMain.Name, restoredMain.ParameterCount, restoredRoute);
    }

    private sealed record Executable(string Name, BytecodeModule Module, string Main, int ParameterCount, DecodedRoute Decoded);
    private sealed record DecodedRoute(string hex, int? argumentSlot, string target, int argc);

    private static DecodedRoute Decode(byte[] bytes, string[] strings, string expectedTarget, bool hasArg)
    {
        int callOffset = hasArg ? 5 : 0;
        Assert.Equal(callOffset + 10, bytes.Length);
        int? slot = null;
        if (hasArg)
        {
            Assert.Equal((byte)0x12, bytes[0]);
            slot = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4));
            Assert.Equal<int?>(0, slot);
        }
        Assert.Equal((byte)0x73, bytes[callOffset]);
        Assert.Equal((byte)0x75, bytes[callOffset + 9]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 1, 4));
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 5, 4));
        Assert.Equal(0, targetIndex);
        Assert.Equal(hasArg ? 1 : 0, argc);
        Assert.Equal(expectedTarget, Assert.Single(strings));
        string target = strings[targetIndex];
        string hex = Convert.ToHexString(bytes).ToLowerInvariant();
        Assert.Equal(hasArg ? "120000000073000000000100000075" : "73000000000000000075", hex);
        return new(hex, slot, target, argc);
    }

    private static TsValue FromBits(long bits) => TsValue.FromFloat64(BitConverter.Int64BitsToDouble(bits));
    private static string Hex(long bits) => bits.ToString("X16", CultureInfo.InvariantCulture);

    private void Write(object record) => output.WriteLine("C251_MATH_COS_NULL " + JsonSerializer.Serialize(record));
}
