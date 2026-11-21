using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortUndefinedComparatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("undefined", "string", "\"c\", \"a\", \"b\"", "undefined", Opcode.LoadConst_Void, "a,b,c")]
    [InlineData("omitted", "string", "\"c\", \"a\", \"b\"", "", null, "a,b,c")]
    [InlineData("callable", "number", "1, 3, 2", "(left: number, right: number): number => right - left", Opcode.LoadFunc, "3,2,1")]
    [InlineData("explicit-null", "string", "\"c\", \"a\", \"b\"", "null", Opcode.LoadConst_Null, null)]
    public void ArraySort_DistinguishesUndefinedFromNull(
        string row, string type, string input, string argument, Opcode? comparatorLoad, string? expected)
    {
        var source = $$"""
            function main(): string {
                const values: {{type}}[] = [{{input}}];
                values.sort({{argument}});
                return values.join(",");
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions,
            instruction => instruction.Opcode == Opcode.Call && Equals(instruction.OperandObject, "Array::sort"));
        Assert.Equal(comparatorLoad.HasValue ? 2 : 1, call.Operand1);
        var callIndex = Array.IndexOf(instructions, call);
        // The direct comparator expression, when present, pushes the last call argument.
        var comparator = "omitted";
        if (comparatorLoad is { } opcode)
        {
            var load = instructions[callIndex - 1];
            Assert.Equal(opcode, load.Opcode);
            comparator = load.Opcode.ToString();
            if (opcode == Opcode.LoadFunc)
            {
                var name = Assert.IsType<string>(load.OperandObject);
                Assert.Single(ir.Functions, function => function.Name == name);
                comparator += $"({name})";
            }
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"Array.sort row={row}; parser/binder=0/0; target=Array::sort; argc={call.Operand1}; comparator={comparator}; expected={expected ?? "Value 'null' is not callable"}");
        TsValue result;
        try
        {
            result = vm.Execute(bytecode, "main", []);
        }
        catch (Exception error)
        {
            output.WriteLine(error.ToString());
            if (expected is not null)
                throw;

            var outer = Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("Failed to compare two elements in the array.", outer.Message);
            var inner = Assert.IsType<InvalidOperationException>(outer.InnerException);
            Assert.Equal("Value 'null' is not callable", inner.Message);
            Assert.Contains("Interpreter.InvokeCallable(", inner.StackTrace!);
            Assert.Contains("System.Array.Sort", outer.StackTrace!);
            return;
        }

        var actual = Assert.IsType<TsStringValue>(result).Value;
        output.WriteLine($"Array.sort row={row}; actual=[{actual}]");
        Assert.Equal(expected, actual);
    }
}
