using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayForEachCallbackReceiverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dense-identity", """
        export function main(): string {
            const values: number[] = [2, 4];
            const visits: string[] = [];
            values.forEach((value: number, index: number, source: number[]): void => {
                if (values === source) {
                    visits.push('same');
                } else {
                    visits.push('other');
                }
                visits.push(value + ':' + index);
            });
            return visits.join('|');
        }
        """, "same|2:0|same|4:1")]
    [InlineData("sparse-identity", """
        export function main(): string {
            const values: number[] = new Array(3);
            values[0] = 2;
            values[2] = 4;
            const visits: string[] = [];
            values.forEach((value: number, index: number, source: number[]): void => {
                visits.push(value + ':' + index);
                if (source === values) visits.push('same');
            });
            return visits.join('|');
        }
        """, "2:0|same|4:2|same")]
    [InlineData("receiver-mutation", """
        export function main(): string {
            const values: number[] = [2, 4];
            const visits: number[] = [];
            values.forEach((value: number, index: number, source: number[]): void => {
                visits.push(value);
                visits.push(index);
                if (index === 0 && source === values) {
                    source[1] = 7;
                    source.push(9);
                }
            });
            return visits.join('|') + '/' + values.join('|');
        }
        """, "2|0|7|1/2|7|9")]
    [InlineData("short-control", """
        export function main(): string {
            const values: number[] = [2, 4];
            const visits: number[] = [];
            values.forEach((value: number, index: number): void => {
                visits.push(value);
                visits.push(index);
            });
            return visits.join('|');
        }
        """, "2|0|4|1")]
    public void ArrayForEach_PassesOriginalReceiverAndPreservesTraversal(string row, string source, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.forEach row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::forEach");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::forEach", target);
        Assert.Equal(2, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"Array.forEach row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.forEach row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.forEach row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsStringValue)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsStringValue>(result).Value);
    }
}
