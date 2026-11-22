using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifyArrayIndexOrderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("mixed-order", "({ label: 7, \"10\": 10, \"2\": 2, apple: 1, \"0\": 0 })", "{\"0\":0,\"2\":2,\"10\":10,\"label\":7,\"apple\":1}")]
    [InlineData("nested-omit", "([{ \"02\": 3, \"10\": 10, \"1\": value, \"2\": 2, label: null }])", "[{\"2\":2,\"10\":10,\"02\":3,\"label\":null}]")]
    [InlineData("index-boundary-escape", "({ \"4294967295\": 7, \"4294967294\": \"line\\nbreak\", \"line\\nkey\": -3.5, \"2\": 2, \"-0\": 8 })", "{\"2\":2,\"4294967294\":\"line\\nbreak\",\"4294967295\":7,\"line\\nkey\":-3.5,\"-0\":8}")]
    [InlineData("nonindex-control", "({ zebra: 1, \"02\": 2, alpha: 3, hidden: value })", "{\"zebra\":1,\"02\":2,\"alpha\":3}")]
    [InlineData("already-ordered-control", "({ \"2\": 2, \"10\": 10, label: 7 })", "{\"2\":2,\"10\":10,\"label\":7}")]
    [InlineData("array-control", "([10, 2])", "[10,2]")]
    public void JsonStringify_OrdersOrdinaryObjectArrayIndexKeys(string row, string expression, string expected)
    {
        var source = $"function main(value: number | undefined): string {{ return JSON.stringify({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var parserCount = parser.Diagnostics.Count;
        var binderCount = binder.Diagnostics.All.Count;
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("JSON::stringify", target);
        Assert.Equal(1, call.Operand1);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is Opcode.CallDynamic or Opcode.CallDynamicArray);
        var bytecode = BytecodeCompiler.Compile(ir);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsStringValue>(vm.Execute(bytecode, "main", [TsValue.Void]));
        var actual = result.Value;
        output.WriteLine($"JSON.stringify row={row}; parser/binder={parserCount}/{binderCount}; opcode={call.Opcode}; target={target}; argc={call.Operand1}; type={result.GetType().Name}; expected={expected}; actual={actual}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"JSON.stringify array-index order mismatch: row={row}; expected={expected}; actual={actual}");
    }
}
