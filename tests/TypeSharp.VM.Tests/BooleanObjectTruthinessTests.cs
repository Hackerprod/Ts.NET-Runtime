using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class BooleanObjectTruthinessTests(ITestOutputHelper output)
{
    private const string Source = "function main(input: any): bool { return Boolean(input); }";
    private const string FactorySource = "class Example {} function make(): any { let instance = new Example(); return instance; }";

    [Theory]
    [InlineData("empty-object", true)]
    [InlineData("zero-field-object", true)]
    [InlineData("class-instance", true)]
    [InlineData("array-control", true)]
    [InlineData("nan-control", false)]
    public void Boolean_ReturnsTrueForObjectValues(string row, bool expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal(TsType.Bool, main.ReturnType);
        var parameter = Assert.Single(main.Parameters);
        Assert.Equal("input", parameter.Name);
        Assert.False(parameter.IsRest);
        Assert.IsType<TsAnyType>(parameter.Type);
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        string target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Boolean", target);
        Assert.Equal(1, call.Operand1);
        var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
        int callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= 1);
        var load = block.Instructions[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);

        var zero = new TsInt32Value(0);
        object? factory = null;
        TsValue input;
        switch (row)
        {
            case "empty-object":
            case "zero-field-object":
                var obj = new TsObject("Object");
                if (row == "zero-field-object") obj.SetField("x", zero);
                input = TsValue.FromObject(obj);
                break;
            case "class-instance":
                input = MakeClass(out factory);
                break;
            case "array-control":
                var array = new TsArray();
                array.Add(zero);
                input = new TsArrayValue(array);
                break;
            case "nan-control":
                input = TsValue.FromFloat64(double.NaN);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
        TsValue[] args = [input];
        object? backing = Backing(input);
        var before = Snapshot(row, input, zero);

        var code = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(code, "main", args); });
        var afterInput = Assert.Single(args);
        var after = Snapshot(row, afterInput, zero);
        bool sameWrapper = ReferenceEquals(input, afterInput);
        bool? sameBacking = backing is null ? null : ReferenceEquals(backing, Backing(afterInput));
        var observation = new Dictionary<string, object?>
        {
            ["row"] = row, ["source"] = Source,
            ["parserCount"] = parser.Diagnostics.Count(), ["binderCount"] = binder.Diagnostics.All.Count(),
            ["opcode"] = call.Opcode.ToString(), ["target"] = target, ["argc"] = call.Operand1,
            ["inputIRType"] = parameter.Type.Name, ["inputOp"] = load.Opcode.ToString(), ["inputArg"] = load.Operand0,
            ["inputType"] = input.GetType().Name, ["inputTag"] = input.ValueType.ToString(),
            ["before"] = before, ["after"] = after, ["sameWrapper"] = sameWrapper, ["sameBacking"] = sameBacking,
            ["escaped"] = escaped?.GetType().FullName, ["actualType"] = result?.GetType().Name,
            ["actual"] = (result as TsBoolValue)?.Value, ["expectedType"] = nameof(TsBoolValue), ["expected"] = expected
        };
        if (factory is not null) observation.Add("factory", factory);
        output.WriteLine("C103 Boolean object row " + JsonSerializer.Serialize(observation));
        Assert.Null(escaped);
        Assert.True(sameWrapper);
        Assert.Equal(backing is null ? (bool?)null : true, sameBacking);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        bool actual = Assert.IsType<TsBoolValue>(result).Value;
        Assert.True(expected == actual,
            $"C103_BOOLEAN_OBJECT_RESULT_MISMATCH {row}: expected TsBoolValue {JsonSerializer.Serialize(expected)}; actual TsBoolValue {JsonSerializer.Serialize(actual)}");
    }

    private static TsObjectValue MakeClass(out object factory)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(FactorySource).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var make = Assert.Single(ir.Functions, f => f.Name == "make");
        var create = Assert.Single(make.Blocks.SelectMany(b => b.Instructions), op => op.Opcode == Opcode.NewObject);
        string target = Assert.IsType<string>(create.OperandObject);
        Assert.Equal("Example", target);
        Assert.Equal(0, create.Operand1);
        var instance = Assert.IsType<TsObjectValue>(new TypeSharp.VM.Interpreter.Interpreter()
            .Execute(BytecodeCompiler.Compile(ir), "make"));
        Assert.Equal(TsValueType.Object, instance.ValueType);
        Assert.Equal("Example", instance.Value.TypeName);
        Assert.Empty(instance.Value.Fields);
        factory = new
        {
            source = FactorySource, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            opcode = create.Opcode.ToString(), target, argc = create.Operand1, returnedType = instance.GetType().Name,
            returnedTag = instance.ValueType.ToString(), backingType = instance.Value.GetType().Name, typeName = instance.Value.TypeName
        };
        return instance;
    }

    private static object? Backing(TsValue input) => input switch
    {
        TsObjectValue obj => obj.Value,
        TsArrayValue array => array.Value,
        _ => null
    };

    private static object Snapshot(string row, TsValue input, TsInt32Value zero)
    {
        if (row == "array-control")
        {
            var array = Assert.IsType<TsArrayValue>(input).Value;
            Assert.Equal(TsValueType.Array, input.ValueType);
            Assert.Equal(1, array.Count);
            var entry = Assert.IsType<TsInt32Value>(array.Get(0));
            Assert.Same(zero, entry);
            Assert.Equal(0, entry.Value);
            return new { entries = new[] { new { type = entry.GetType().Name, value = entry.Value } } };
        }
        if (row == "nan-control")
        {
            Assert.Equal(TsValueType.Float64, input.ValueType);
            Assert.True(double.IsNaN(Assert.IsType<TsFloat64Value>(input).Value));
            return new { value = "NaN" };
        }
        var obj = Assert.IsType<TsObjectValue>(input).Value;
        Assert.Equal(TsValueType.Object, input.ValueType);
        Assert.Equal(row == "class-instance" ? "Example" : "Object", obj.TypeName);
        Assert.Equal(row == "zero-field-object" ? 1 : 0, obj.Fields.Count);
        var fields = obj.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            Assert.Equal("x", pair.Key);
            var entry = Assert.IsType<TsInt32Value>(pair.Value);
            Assert.Same(zero, entry);
            Assert.Equal(0, entry.Value);
            return new { key = pair.Key, type = entry.GetType().Name, value = entry.Value };
        }).ToArray();
        return new { typeName = obj.TypeName, fields };
    }
}
