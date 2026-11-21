using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArraySetUndefinedResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("typed-array-explicit-offset", true, 3, "5,42,99,20")]
    [InlineData("numeric-array-omitted-offset", false, 2, "42,99,15,20")]
    public void Uint8ArraySet_CopiesSourceAndReturnsUndefined(string row, bool typedSource, int expectedArity, string expectedBytes)
    {
        var parameter = typedSource ? ", source: Uint8Array" : "";
        var sourceSetup = typedSource ? "" : "let source: number[] = [42, 99];";
        var arguments = typedSource ? "source, 1" : "source";
        var script = $$"""
            function main(target: Uint8Array{{parameter}}): int32 {
                {{sourceSetup}}
                let result: any = target.set({{arguments}});
                if (result === undefined) {
                    if (result === null) return 3;
                    return 1;
                }
                if (result === null) return 2;
                return 3;
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(script).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Uint8Array::set", target);
        Assert.Equal(expectedArity, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var bytes = new TsUint8ArrayValue([5, 10, 15, 20]);
        TsValue[] args = typedSource ? [bytes, new TsUint8ArrayValue([42, 99])] : [bytes];
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(bytecode, "main", args);
        var actualBytes = string.Join(",", Enumerable.Range(0, bytes.Length).Select(bytes.Get));
        Assert.Equal(expectedBytes, actualBytes);

        const int expectedTag = 1;
        var actualTag = Assert.IsType<TsInt32Value>(result).Value;
        output.WriteLine($"Uint8Array.set row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; expected=[{expectedBytes}]; actual=[{actualBytes}]; expectedTag={expectedTag}; actualTag={actualTag}");
        Assert.Equal(expectedTag, actualTag);
    }
}
