using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFindIndexCallbackReceiverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dense", """
        export function main(): string {
          const a: number[] = [2, 4, 6];
          let trace: string = '';
          const found: any = a.findIndex((v: number, i: number, source: number[]): boolean => {
            if (source === a) trace = trace + 'same:';
            else if (source === undefined) trace = trace + 'missing:';
            else trace = trace + 'other:';
            trace = trace + i + ':' + v + '|';
            return v === 4;
          });
          return trace + found;
        }
        """, "same:0:2|same:1:4|1")]
    [InlineData("sparse", """
        export function main(): string {
          const a: any[] = [0, 4, 6];
          delete a[0];
          let trace: string = '';
          const found: any = a.findIndex((v: any, i: number, source: any[]): boolean => {
            if (source === a) trace = trace + 'same:';
            else if (source === undefined) trace = trace + 'missing:';
            else trace = trace + 'other:';
            if (v === undefined) trace = trace + i + ':undefined|';
            else trace = trace + i + ':' + v + '|';
            return v === 4;
          });
          return trace + found;
        }
        """, "same:0:undefined|same:1:4|1")]
    [InlineData("no-match", """
        export function main(): string {
          const a: any[] = [9, 11, 13];
          let trace: string = '';
          const index: number = a.findIndex((value: any, i: number, source: any[]): boolean => {
            if (i === 0 && source === a) {
              source[1] = 21;
              delete source[2];
              source.push(17);
            }
            trace = trace + i + '=' + (value === undefined ? 'hole' : value) + ';';
            return value === 17;
          });
          return trace + 'index=' + index + ';array=' + a.join(',');
        }
        """, "0=9;1=21;2=hole;index=-1;array=9,21,,17")]
    [InlineData("no-match-control", """
        export function main(): string {
          const a: any[] = [9, 11, 13];
          let trace: string = '';
          const index: number = a.findIndex((value: any, i: number): boolean => {
            if (i === 0 && true) {
              a[1] = 21;
              delete a[2];
              a.push(17);
            }
            trace = trace + i + '=' + (value === undefined ? 'hole' : value) + ';';
            return value === 17;
          });
          return trace + 'index=' + index + ';array=' + a.join(',');
        }
        """, "0=9;1=21;2=hole;index=-1;array=9,21,,17")]
    public void ArrayFindIndex_PassesOriginalReceiverAndPreservesTraversal(string row, string source, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.findIndex row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::findIndex");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::findIndex", target);

        output.WriteLine($"Array.findIndex row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        Assert.Equal(2, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.findIndex row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.findIndex row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsStringValue)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsStringValue>(result).Value);
    }
}
