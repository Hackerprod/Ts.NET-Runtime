using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortDefaultFractionalFloat64OrderTests(ITestOutputHelper output)
{
    private const string NumericComparator = "(a: number, b: number): number => a - b";

    [Fact]
    public void OmittedComparator_UsesFractionalLexicalOrder() =>
        Verify(nameof(OmittedComparator_UsesFractionalLexicalOrder), "number", "",
            PositiveFractions(), [2, 1, 0]);

    [Fact]
    public void UndefinedComparator_UsesMixedNegativeLexicalOrder() =>
        Verify(nameof(UndefinedComparator_UsesMixedNegativeLexicalOrder), "number", "undefined",
            [new TsInt32Value(-2), new TsFloat64Value(-10.5), new TsFloat64Value(-2.5)], [1, 0, 2]);

    [Fact]
    public void ExplicitComparator_RetainsNumericOrder() =>
        Verify(nameof(ExplicitComparator_RetainsNumericOrder), "number", NumericComparator,
            PositiveFractions(), [2, 0, 1]);

    [Fact]
    public void DenseStrings_RetainOrdinalOrder() =>
        Verify(nameof(DenseStrings_RetainOrdinalOrder), "string", "",
            [new TsStringValue("2.5"), new TsStringValue("10.5"), new TsStringValue("1.5")], [2, 1, 0]);

    private static TsValue[] PositiveFractions() =>
        [new TsFloat64Value(2.5), new TsFloat64Value(10.5), new TsFloat64Value(1.5)];

    private void Verify(string row, string type, string argument, TsValue[] originals, int[] expectedRefs)
    {
        var source = $$"""
            function main(Array: {{type}}[]): {{type}}[] { return Array.sort({{argument}}); }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("Array", parameter.Name);
        Assert.False(parameter.IsRest);
        var parameterType = Assert.IsType<TsArrayType>(parameter.Type);
        Assert.False(parameterType.IsReadonly);
        Assert.Equal(type, parameterType.ElementType.Name);
        var returnType = Assert.IsType<TsArrayType>(main.ReturnType);
        Assert.False(returnType.IsReadonly);
        Assert.Equal(parameterType.Name, returnType.Name);

        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic
            or Opcode.CallDynamicArray or Opcode.ReturnVoid);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::sort", target);
        var argc = argument.Length == 0 ? 1 : 2;
        Assert.Equal(argc, call.Operand1);
        var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
        var callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= argc && callIndex + 1 < block.Instructions.Count);
        var load = block.Instructions[callIndex - argc];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        var ret = Assert.Single(ops, op => op.Opcode == Opcode.Return);
        Assert.Same(ret, block.Instructions[callIndex + 1]);

        var comparator = "omitted";
        string? comparatorFunction = null;
        if (argc == 2)
        {
            var comparatorLoad = block.Instructions[callIndex - 1];
            if (argument == "undefined")
            {
                Assert.Equal(Opcode.LoadConst_Void, comparatorLoad.Opcode);
                comparator = comparatorLoad.Opcode.ToString();
            }
            else
            {
                Assert.Equal(Opcode.LoadFunc, comparatorLoad.Opcode);
                var name = Assert.IsType<string>(comparatorLoad.OperandObject);
                var function = Assert.Single(ir.Functions, function => function.Name == name);
                Assert.NotSame(main, function);
                Assert.Equal(new[] { "a", "b" }, function.Parameters.Select(p => p.Name));
                Assert.All(function.Parameters, p => { Assert.Equal("number", p.Type.Name); Assert.False(p.IsRest); });
                Assert.Equal("number", function.ReturnType.Name);
                Assert.Single(function.Blocks.SelectMany(b => b.Instructions),
                    op => op.Opcode is Opcode.Sub_I32 or Opcode.Sub_I64 or Opcode.Sub_U64
                        or Opcode.Sub_F32 or Opcode.Sub_F64 or Opcode.Sub_Decimal);
                comparator = comparatorLoad.Opcode.ToString();
                comparatorFunction = name;
            }
        }

        var backing = new TsArray();
        foreach (var original in originals)
            backing.Add(original);
        var receiver = new TsArrayValue(backing);
        TsValue[] expected = expectedRefs.Select(i => originals[i]).ToArray();
        var before = originals.Select(Capture).ToArray();
        Assert.Equal(originals.Length, backing.Count);
        Assert.All(Enumerable.Range(0, backing.Count), i =>
        {
            Assert.False(backing.IsHole(i));
            Assert.Same(originals[i], backing.Get(i));
        });

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiledMain = Assert.Single(bytecode.Functions, function => function.Name == main.Name);
        Assert.NotEmpty(compiledMain.Instructions);
        Assert.Equal(1, compiledMain.ParameterCount);
        if (comparatorFunction is not null)
        {
            var compiledComparator = Assert.Single(bytecode.Functions, function => function.Name == comparatorFunction);
            Assert.NotEmpty(compiledComparator.Instructions);
            Assert.Equal(2, compiledComparator.ParameterCount);
        }
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, compiledMain.Name, [receiver]); });
        Assert.Null(escaped);
        var returnedArray = Assert.IsType<TsArrayValue>(result);
        Assert.Equal(TsValueType.Array, returnedArray.ValueType);
        Assert.Same(receiver, returnedArray);
        Assert.Same(backing, returnedArray.Value);
        Assert.Equal(originals.Length, backing.Count);
        var actual = Enumerable.Range(0, backing.Count).Select(backing.Get).ToArray();
        Assert.All(Enumerable.Range(0, backing.Count), i => Assert.False(backing.IsHole(i)));
        var actualRefs = actual.Select(value =>
            Array.FindIndex(originals, original => ReferenceEquals(original, value))).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, actualRefs.Order());
        var after = actual.Select((value, slot) => Capture(value, actualRefs[slot])).ToArray();
        Assert.All(after, element => Assert.Equal(before[element.Index], element));

        output.WriteLine("C150_SORT " + JsonSerializer.Serialize(new
        {
            row, source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            entry = compiledMain.Name, parameter = parameter.Name, parameterType = parameterType.Name,
            receiverLoad = load.Opcode.ToString(), receiverSlot = load.Operand0, comparatorLoad = comparator,
            call = call.Opcode.ToString(), target, argc, valueReturn = ret.Opcode.ToString(),
            executionReturnedNormally = escaped is null, returnTag = returnedArray.ValueType.ToString(),
            sameReceiver = ReferenceEquals(receiver, returnedArray), sameBacking = ReferenceEquals(backing, returnedArray.Value),
            count = backing.Count, allPresent = true, originalReferencesOnce = true, before, after, expectedRefs, actualRefs
        }));
        Assert.True(expected.Zip(actual).All(pair => ReferenceEquals(pair.First, pair.Second)),
            $"C150_SORT_ORDER_MISMATCH row={row}; expected=[{string.Join(",", expectedRefs)}]; actual=[{string.Join(",", actualRefs)}]");
    }

    private sealed record ElementSnapshot(int Index, string ClrType, string Tag, string? Raw, long? Float64Bits);

    private static ElementSnapshot Capture(TsValue value, int index) =>
        new(index, value.GetType().Name, value.ValueType.ToString(),
            value is TsFloat64Value number ? number.Value.ToString("R", CultureInfo.InvariantCulture) :
                Convert.ToString(value.RawValue, CultureInfo.InvariantCulture),
            value is TsFloat64Value floating ? BitConverter.DoubleToInt64Bits(floating.Value) : null);
}
