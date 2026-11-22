using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifyUint8ArrayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("root-bytes", "new Uint8Array([20, 5, 90, 1, 4, 3, 6, 2, 8, 7, 9])", "{\"0\":20,\"1\":5,\"2\":90,\"3\":1,\"4\":4,\"5\":3,\"6\":6,\"7\":2,\"8\":8,\"9\":7,\"10\":9}")]
    [InlineData("nested-bytes", "({ payload: new Uint8Array([20, 5, 90]), version: 1 })", "{\"payload\":{\"0\":20,\"1\":5,\"2\":90},\"version\":1}")]
    [InlineData("empty-bytes", "new Uint8Array()", "{}")]
    [InlineData("ordinary-array", "[20, 5, 90]", "[20,5,90]")]
    [InlineData("ordinary-object", "({ \"0\": 20, \"1\": 5, \"2\": 90 })", "{\"0\":20,\"1\":5,\"2\":90}")]
    public void JsonStringify_UsesIndexedPropertiesForNativeUint8Array(string row, string expression, string expected)
    {
        var source = $"function main(): string {{ return JSON.stringify({expression}); }}";
        output.WriteLine($"row={row}; source={source}");
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        output.WriteLine($"row={row}; parser={parser.Diagnostics.Count} [{string.Join(", ", parser.Diagnostics)}]; binder={binder.Diagnostics.All.Count} [{string.Join(", ", binder.Diagnostics.All)}]");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode is Opcode.Call or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        Assert.Equal(Opcode.Call, call.Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("JSON::stringify", target);
        Assert.Equal(1, call.Operand1);
        output.WriteLine($"row={row}; call={call.Opcode}; target={target}; argc={call.Operand1}");

        var constructors = instructions.Where(instruction =>
            instruction.Opcode == Opcode.NewObject && instruction.OperandObject is "Uint8Array").ToArray();
        if (row is "ordinary-array" or "ordinary-object")
        {
            Assert.Empty(constructors);
            output.WriteLine($"row={row}; Uint8Array allocation=absent");
        }
        else
        {
            var constructor = Assert.Single(constructors);
            Assert.Equal(row == "empty-bytes" ? 0 : 1, constructor.Operand1);
            output.WriteLine($"row={row}; constructor={constructor.Opcode}; tag={constructor.OperandObject}; arity={constructor.Operand1}");
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsStringValue>(vm.Execute(bytecode, "main", []));
        var actual = result.Value;
        output.WriteLine($"row={row}; type={result.GetType().Name}; expected={expected}; actual={actual}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"C140_UINT8ARRAY_JSON_TEXT_MISMATCH row={row}; expected={expected}; actual={actual}");
    }
}
