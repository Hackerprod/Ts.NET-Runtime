using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFlatMapCallbackReceiverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dense-identity", """
        export function main(): string {
            const values: number[] = [2, 4];
            return values.flatMap((v: number, i: number, source: number[]): string[] => {
                if (source === values) return ['same:' + v + ':' + i];
                return ['other:' + v + ':' + i];
            }).join('|');
        }
        """, "same:2:0|same:4:1")]
    [InlineData("sparse-identity", """
        export function main(): string {
            const values: any[] = [2, 4, undefined, 6];
            delete values[1];
            return values.flatMap((v: any, i: number, source: any[]): string[] => {
                if (source === values) {
                    if (v === undefined) return ['same:U:' + i];
                    return ['same:' + i];
                }
                if (v === undefined) return ['other:U:' + i];
                return ['other:' + i];
            }).join('|');
        }
        """, "same:0|same:U:2|same:3")]
    [InlineData("receiver-mutation", """
        export function main(): string {
            const values: number[] = [2, 4, 6];
            const mapped = values.flatMap((v: number, i: number, source: number[]): number[] => {
                if (i === 0 && source === values) {
                    source[1] = 40;
                    delete source[2];
                    source.push(8);
                }
                return [v, i];
            });
            return mapped.join(',') + '|' + values.join(',');
        }
        """, "2,0,40,1|2,40,,8")]
    [InlineData("short-control", """
        export function main(): string {
            const values: number[] = [2, 4, 6];
            delete values[1];
            return values.flatMap((v: number, i: number): number[] => [v, i]).join(',');
        }
        """, "2,0,6,2")]
    public void ArrayFlatMap_PassesOriginalReceiverAndPreservesTraversal(string row, string source, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.flatMap row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::flatMap");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::flatMap", target);
        Assert.Equal(2, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"Array.flatMap row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.flatMap row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.flatMap row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsStringValue)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsStringValue>(result).Value);
    }
}
