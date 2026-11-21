using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortNaNComparatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("nan", "0.0 / 0.0", "3,1,2")]
    [InlineData("zero", "0", "3,1,2")]
    [InlineData("numeric", "left - right", "1,2,3")]
    public void ArraySort_TreatsNaNComparatorResultAsZero(string row, string expression, string expected)
    {
        var source = $$"""
            function main(): string {
                const values: number[] = [3, 1, 2];
                values.sort((left: number, right: number): number => {{expression}});
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
        Assert.Equal(2, call.Operand1);
        if (row == "nan")
        {
            var load = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.LoadFunc);
            var name = Assert.IsType<string>(load.OperandObject);
            var comparator = Assert.Single(ir.Functions, function => function.Name == name);
            var division = Assert.Single(comparator.Blocks.SelectMany(block => block.Instructions),
                instruction => instruction.Opcode is Opcode.Div_I32 or Opcode.Div_I64 or Opcode.Div_U64
                    or Opcode.Div_F32 or Opcode.Div_F64 or Opcode.Div_Decimal);
            Assert.Equal(Opcode.Div_F64, division.Opcode);
            output.WriteLine($"Array.sort comparator={name}; division={division.Opcode}");
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"Array.sort row={row}; parser/binder=0/0; target=Array::sort; argc={call.Operand1}; expected=[{expected}]");
        TsValue result;
        try
        {
            result = vm.Execute(bytecode, "main", []);
        }
        catch (Exception error)
        {
            output.WriteLine(error.ToString());
            throw;
        }

        var actual = Assert.IsType<TsStringValue>(result).Value;
        output.WriteLine($"Array.sort row={row}; actual=[{actual}]");
        Assert.Equal(expected, actual);
    }
}
