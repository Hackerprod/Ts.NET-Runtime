using System.Buffers.Binary;
using System.Globalization;
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

public sealed class MathAsinNullTests(ITestOutputHelper output)
{
    private const string Target = "Math::asin";

    [Fact]
    public void Asin_ExplicitNullReturnsPositiveZero()
    {
        var value = Compile("asin-value", "c254-math-asin-null.ts",
            "function main(value: any): number { return Math.asin(value); }", 1);
        var omitted = Compile("asin-omitted", "c254-math-asin-omitted.ts",
            "function main(): number { return Math.asin(); }", 0);

        Assert.True(double.IsNaN(Run(value, "explicit-undefined", [TsValue.Void], "Void", null)));
        Assert.True(double.IsNaN(Run(omitted, "omitted", [], "Omitted", null)));
        Assert.Equal(0L, Bits(Run(value, "positive-zero", [TsValue.FromFloat64(0d)],
            "Float64", "0000000000000000")));
        Assert.Equal(long.MinValue,
            Bits(Run(value, "negative-zero", [TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue))],
                "Float64", "8000000000000000")));
        double angle = Run(value, "finite-half", [TsValue.FromFloat64(0.5d)], "Float64", "3FE0000000000000");
        Assert.True(double.IsFinite(angle));
        Assert.InRange(Math.Abs(angle - 0.5235987755982989d), 0d, 1e-12);

        double explicitNull = Run(value, "explicit-null", [TsValue.Null], "Null", null);
        Assert.True(Bits(explicitNull) == 0L, "C254_ASIN_NULL_EXPECTED_PZERO");
    }

    private Route Compile(string name, string file, string source, int arity)
    {
        var tokens = new Lexer(source, file).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var mainToken = Assert.Single(tokens.Where(token => token.Kind == TokenKind.Identifier && token.Text == "main"));
        Assert.Equal(file, mainToken.Location.Source);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse(file);
        Assert.Equal(file, syntax.FileName);
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Equal(file, bound.FileName);
        Assert.Empty(binder.Diagnostics.All);
        var main = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", main.Symbol.Name);
        Assert.Same(TsType.Number, main.Symbol.Type);
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Number, call.Type);
        Assert.False(call.IsNullConditional);
        var access = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(access.IsNullConditional);
        var receiver = Assert.IsType<BoundVariableExpression>(access.Object);
        var math = Assert.IsType<ClassSymbol>(receiver.Symbol);
        Assert.Equal("Math", math.Name);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("asin", method.Name);
        Assert.Equal(math.Name, method.DeclaringClassName);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Same(TsType.Number, method.Type);
        var signature = Assert.IsType<TsFunctionType>(access.Type);
        Assert.Same(TsType.Number, signature.ReturnType);
        string boundTarget = $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}";
        Assert.Equal(Target, boundTarget);
        if (arity == 1)
        {
            var parameter = Assert.IsType<ParameterSymbol>(Assert.Single(main.Symbol.Parameters));
            Assert.Equal("value", parameter.Name);
            Assert.Same(TsType.Any, parameter.Type);
            var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(call.Arguments));
            Assert.Same(parameter, argument.Symbol);
            Assert.Same(TsType.Any, argument.Type);
        }
        else
        {
            Assert.Empty(main.Symbol.Parameters);
            Assert.Empty(call.Arguments);
        }

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(main.Symbol.Name, irMain.Name);
        var ops = Assert.Single(irMain.Blocks).Instructions;
        Assert.Equal(arity == 1 ? new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return }
            : new[] { Opcode.Call, Opcode.Return }, ops.Select(op => op.Opcode));
        if (arity == 1)
            Assert.Equal(0, ops[0].Operand0);
        Assert.Equal(Target, Assert.IsType<string>(ops[arity].OperandObject));
        Assert.Equal(arity, ops[arity].Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(compiled);
        var encoded = Assert.Single(compiled.Functions);
        Assert.Equal(irMain.Name, encoded.Name);
        Assert.Equal(arity, encoded.ParameterCount);
        var bytes = encoded.Instructions;
        int callOffset = arity == 1 ? 5 : 0;
        int? argumentSlot = null;
        Assert.Equal(callOffset + 10, bytes.Length);
        if (arity == 1)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[0]);
            argumentSlot = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1, 4));
            Assert.Equal<int?>(0, argumentSlot);
        }
        Assert.Equal(Opcodes.Call, bytes[callOffset]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 1, 4));
        Assert.Equal(0, targetIndex);
        string target = Assert.Single(encoded.StringConstants);
        Assert.Equal(Target, target);
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(callOffset + 5, 4));
        Assert.Equal(arity, argc);
        Assert.Equal(Opcodes.Return, bytes[callOffset + 9]);

        Write(new
        {
            kind = "route",
            route = name,
            lexerFile = mainToken.Location.Source,
            parsedFile = syntax.FileName,
            boundFile = bound.FileName,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count(),
            main = encoded.Name,
            parameterCount = encoded.ParameterCount,
            irTarget = boundTarget,
            irArgc = ops[arity].Operand1,
            argumentSlot,
            target,
            argc,
            compiledHex = Convert.ToHexString(bytes).ToLowerInvariant()
        });
        return new(name, compiled, encoded.Name, argc);
    }

    private double Run(Route route, string row, TsValue[] args, string expectedType, string? expectedInputBits)
    {
        Assert.Equal(route.Argc, args.Length);
        string inputType = "Omitted";
        string? inputBits = null;
        if (args.Length != 0)
        {
            TsValue arg = Assert.Single(args);
            inputType = arg.ValueType.ToString();
            switch (arg)
            {
                case TsNull suppliedNull:
                    Assert.Same(TsValue.Null, suppliedNull);
                    Assert.Null(suppliedNull.RawValue);
                    break;
                case TsVoid suppliedVoid:
                    Assert.Same(TsValue.Void, suppliedVoid);
                    Assert.Null(suppliedVoid.RawValue);
                    break;
                default:
                    inputBits = Hex(Bits(Assert.IsType<TsFloat64Value>(arg).Value));
                    break;
            }
        }
        Assert.Equal(expectedType, inputType);
        Assert.Equal(expectedInputBits, inputBits);

        var returned = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, args);
        var result = Assert.IsType<TsFloat64Value>(returned);
        Assert.Equal(TsValueType.Float64, result.ValueType);
        string observedBits = Hex(Bits(result.Value));
        Write(new
        {
            kind = "result",
            row,
            route = route.Name,
            argc = route.Argc,
            inputType,
            inputBits,
            outputType = result.ValueType.ToString(),
            observed = double.IsNaN(result.Value) ? "NaN" : observedBits,
            observedBits
        });
        return result.Value;
    }

    private sealed record Route(string Name, BytecodeModule Module, string Main, int Argc);
    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    private static string Hex(long bits) => bits.ToString("X16", CultureInfo.InvariantCulture);
    private void Write(object record) => output.WriteLine("C254_MATH_ASIN_NULL " + JsonSerializer.Serialize(record));
}
