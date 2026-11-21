using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayReduceCallbackReceiverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("seeded-identity", """
        export function main(): string {
            const values: number[] = [2, 4];
            let trace: string = '';
            const total: number = values.reduce((acc: number, v: number, i: number, source: number[]): number => {
                if (source === values) trace = trace + 'same:' + acc + ':' + v + ':' + i + '|';
                else trace = trace + 'other:' + acc + ':' + v + ':' + i + '|';
                return acc + v;
            }, 10);
            return trace + total;
        }
        """, 3, "same:10:2:0|same:12:4:1|16")]
    [InlineData("unseeded-sparse-identity", """
        export function main(): string {
            const values: number[] = [0, 2, 0, 6];
            delete values[0];
            delete values[2];
            let trace: string = '';
            const total: number = values.reduce((acc: number, v: number, i: number, source: number[]): number => {
                if (source === values) trace = trace + 'same:' + acc + ':' + v + ':' + i;
                else trace = trace + 'other:' + acc + ':' + v + ':' + i;
                return acc + v;
            });
            return trace + '|' + total;
        }
        """, 2, "same:2:6:3|8")]
    [InlineData("receiver-mutation", """
        export function main(): string {
            const values: number[] = [2, 4, 6];
            let trace: string = '';
            const total: number = values.reduce((acc: number, v: number, i: number, source: number[]): number => {
                if (i === 0 && source === values) {
                    source[1] = 40;
                    delete source[2];
                    source.push(8);
                }
                trace = trace + i + ':' + v + '|';
                return acc + v;
            }, 10);
            return trace + total + '|' + values.join(',');
        }
        """, 3, "0:2|1:40|52|2,40,,8")]
    [InlineData("short-callback-control", """
        export function main(): string {
            const values: number[] = [2, 4, 6];
            delete values[1];
            let trace: string = '';
            const total: number = values.reduce((acc: number, v: number, i: number): number => {
                trace = trace + i + ':' + v + '|';
                return acc + v;
            }, 10);
            return trace + total;
        }
        """, 3, "0:2|2:6|18")]
    public void ArrayReduce_PassesOriginalReceiverAndPreservesTraversal(string row, string source, int expectedArgc, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.reduce row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::reduce");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::reduce", target);

        output.WriteLine($"Array.reduce row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        Assert.Equal(expectedArgc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.reduce row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.reduce row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsStringValue)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsStringValue>(result).Value);
    }
}
