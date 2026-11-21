using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayEveryCallbackReceiverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dense", """
        export function main(): string {
            const values: number[] = [2, 4, 6];
            let trace: string = '';
            const matched: boolean = values.every((v: number, i: number, source: number[]): boolean => {
                if (source === values) trace = trace + 'same:' + i + ':' + v + '|';
                else trace = trace + 'other:' + i + ':' + v + '|';
                return v !== 4;
            });
            return trace + matched;
        }
        """, "same:0:2|same:1:4|false")]
    [InlineData("sparse", """
        export function main(): string {
            const values: number[] = [0, 2, 0, 6];
            delete values[0];
            delete values[2];
            let trace: string = '';
            const matched: boolean = values.every((v: number, i: number, source: number[]): boolean => {
                if (source === values) trace = trace + 'same:' + i + ':' + v + '|';
                else trace = trace + 'other:' + i + ':' + v + '|';
                return v > 0;
            });
            return trace + matched;
        }
        """, "same:1:2|same:3:6|true")]
    [InlineData("mutation", """
        export function main(): string {
            const values: number[] = [2, 4, 6];
            let trace: string = '';
            const matched: boolean = values.every((v: number, i: number, source: number[]): boolean => {
                if (i === 0 && source === values) {
                    source[1] = 40;
                    delete source[2];
                    source.push(8);
                }
                trace = trace + i + ':' + v + '|';
                return v !== 8;
            });
            return trace + matched + '|' + values.join(',');
        }
        """, "0:2|1:40|true|2,40,,8")]
    [InlineData("control", """
        export function main(): string {
            const values: number[] = [2, 0, 6, 8];
            delete values[1];
            let trace: string = '';
            const matched: boolean = values.every((v: number, i: number): boolean => {
                trace = trace + i + ':' + v + '|';
                return v !== 6;
            });
            return trace + matched;
        }
        """, "0:2|2:6|false")]
    public void ArrayEvery_PassesOriginalReceiverAndPreservesTraversal(string row, string source, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.every row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::every");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::every", target);

        output.WriteLine($"Array.every row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        Assert.Equal(2, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.every row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.every row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsStringValue)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsStringValue>(result).Value);
    }
}
