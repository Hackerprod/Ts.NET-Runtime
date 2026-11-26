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

public sealed class MathTanNullTests(ITestOutputHelper output)
{
    private const string PiOverFour = "3FE921FB54442D18";
    private const string NaN = "NaN";
    private const long PiOverFourBits = 0x3FE921FB54442D18L;

    [Fact]
    public void Tan_ExplicitNullReturnsPositiveZero()
    {
        var tanValue = BuildRoute("tan-value", "c252-math-tan-null.ts",
            "function main(value: any): number { return Math.tan(value); }", "tan", true);
        var tanOmitted = BuildRoute("tan-omitted", "c252-math-tan-omitted.ts",
            "function main(): number { return Math.tan(); }", "tan", false);

        var rows = new (string Row, Executable Route, TsValue[] Args, string InputType, object? Input)[]
        {
            ("explicit-null", tanValue, [TsValue.Null], "Null", null),
            ("explicit-undefined", tanValue, [TsValue.Void], "Void", "undefined"),
            ("omitted", tanOmitted, [], "Omitted", null),
            ("positive-zero", tanValue, [FromBits(0L)], "Float64", "0000000000000000"),
            ("negative-zero", tanValue, [FromBits(long.MinValue)], "Float64", "8000000000000000"),
            ("finite-pi-over-four", tanValue, [FromBits(PiOverFourBits)], "Float64", PiOverFour)
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
        Assert.Equal(0L, observations[3].Bits);
        Assert.Equal(long.MinValue, observations[4].Bits);
        Assert.True(double.IsFinite(observations[5].Value));
        Assert.True(Math.Abs(observations[5].Value - 1d) <= 1e-12);

        Assert.True(observations[0].Bits == 0L, "C252_TAN_NULL_EXPECTED_PZERO");
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
        var compiledMain = Assert.Single(compiled.Functions);
        Assert.Equal(irMain.Name, compiledMain.Name);
        Assert.Equal(arity, compiledMain.ParameterCount);
        var compiledRoute = Decode(compiledMain.Instructions, compiledMain.StringConstants, target, hasArg);

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
            main = compiledMain.Name,
            parameterCount = compiledMain.ParameterCount,
            argumentSlot = compiledRoute.argumentSlot,
            target = compiledRoute.target,
            argc = compiledRoute.argc,
            compiledHex = compiledRoute.hex
        });
        return new Executable(route, compiled, compiledMain.Name, compiledMain.ParameterCount, compiledRoute);
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

    private void Write(object record) => output.WriteLine("C252_MATH_TAN_NULL " + JsonSerializer.Serialize(record));
}
