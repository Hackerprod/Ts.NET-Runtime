using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFlatNaNDepthTests(ITestOutputHelper output)
{
    [Fact]
    public void Flat_NumericNaNDepthUsesZero()
    {
        const string source = "function main(a: any[], depth: number): any[] { return a.flat(depth); }";
        const string input = "[H1[2[3]]4]";
        const string expected = "[1[2[3]]4]";
        var tokens = new Lexer(source).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var block = Assert.Single(main.Blocks, b => b.Instructions.Any(i => i.Opcode == Opcode.Call));
        var call = Assert.Single(block.Instructions, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::flat", target);
        Assert.Equal(2, call.Operand1);
        var callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= 2);
        var receiverLoad = block.Instructions[callIndex - 2];
        var depthLoad = block.Instructions[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, receiverLoad.Opcode);
        Assert.Equal(0, receiverLoad.Operand0);
        Assert.Equal(Opcode.LoadArg, depthLoad.Opcode);
        Assert.Equal(1, depthLoad.Operand0);
        var bytecode = BytecodeCompiler.Compile(ir);
        var mismatches = new List<string>();
        foreach (var (row, depth, kind) in new (string, TsValue, Type)[]
        {
            ("float64-nan", TsValue.FromFloat64(double.NaN), typeof(TsFloat64Value)),
            ("float32-nan", TsValue.FromFloat32(float.NaN), typeof(TsFloat32Value)),
            ("int32-zero", TsValue.FromInt32(0), typeof(TsInt32Value))
        })
        {
            Assert.Equal(kind, depth.GetType());
            var typedDepth = depth switch
            {
                TsFloat64Value f64 when double.IsNaN(f64.Value) => "NaN",
                TsFloat32Value f32 when float.IsNaN(f32.Value) => "NaN",
                TsInt32Value i when i.Value == 0 => "0",
                _ => throw new InvalidOperationException("Unexpected depth fixture value")
            };
            var survivor = ArrayOf(TsValue.FromInt32(2), ArrayOf(TsValue.FromInt32(3)));
            var hull = new TsArray();
            hull.SetHole(0);
            hull.Add(TsValue.FromInt32(1));
            hull.Add(survivor);
            hull.Add(TsValue.FromInt32(4));
            var receiver = new TsArrayValue(hull);
            var before = Snapshot(hull);
            Assert.Equal(input, before);
            Assert.True(hull.IsHole(0));
            TsValue? actual = null;
            Exception? escaped = null;
            try
            {
                actual = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", [receiver, depth]);
            }
            catch (Exception exception)
            {
                escaped = exception;
            }

            var after = Snapshot(hull);
            var result = actual as TsArrayValue;
            var snapshot = result is null ? null : Snapshot(result.Value);
            bool? dense = result is null ? null
                : result.Value.Count == 3 && Enumerable.Range(0, result.Value.Count).All(i => !result.Value.IsHole(i));
            bool? freshWrapper = result is null ? null : !ReferenceEquals(receiver, result);
            bool? freshBacking = result is null ? null : !ReferenceEquals(hull, result.Value);
            var nested = result is not null && result.Value.Count > 1 ? result.Value.Get(1) as TsArrayValue : null;
            bool? sameNestedWrapper = result is null ? null : ReferenceEquals(survivor, nested);
            bool? sameNestedBacking = result is null ? null : ReferenceEquals(survivor.Value, nested?.Value);
            output.WriteLine("C182 Array.flat depth " + JsonSerializer.Serialize(new
            {
                row, source, lexerErrors, parserErrors = parser.Diagnostics.Count,
                binderErrors = binder.Diagnostics.All.Count, target, argc = call.Operand1,
                receiverOp = receiverLoad.Opcode.ToString(), receiverArg = receiverLoad.Operand0,
                depthOp = depthLoad.Opcode.ToString(), depthArg = depthLoad.Operand0,
                depthType = depth.GetType().Name, depthValue = typedDepth, before, after,
                resultType = actual?.GetType().Name, snapshot, dense, freshWrapper, freshBacking,
                sameNestedWrapper, sameNestedBacking, exceptionType = escaped?.GetType().FullName,
                exceptionMessage = escaped?.Message, expected
            }));
            if (escaped is not null || after != input || actual?.GetType() != typeof(TsArrayValue)
                || snapshot != expected || dense != true || freshWrapper != true || freshBacking != true
                || sameNestedWrapper != true || sameNestedBacking != true)
                mismatches.Add(row);
        }

        Assert.True(mismatches.Count == 0, "C182_SEMANTIC_MISMATCHES " + string.Join(",", mismatches));
    }

    private static TsArrayValue ArrayOf(params TsValue[] values)
    {
        var array = new TsArray();
        foreach (var value in values)
            array.Add(value);
        return new TsArrayValue(array);
    }

    private static string Snapshot(TsArray array) => "[" + string.Concat(
        Enumerable.Range(0, array.Count).Select(i => array.IsHole(i) ? "H" : array.Get(i) switch
        {
            TsArrayValue nested => Snapshot(nested.Value),
            TsInt32Value value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "?"
        })) + "]";
}
