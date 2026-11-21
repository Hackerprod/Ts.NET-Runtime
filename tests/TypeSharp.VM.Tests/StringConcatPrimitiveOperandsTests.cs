using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringConcatPrimitiveOperandsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("booleans", """export function main(): string { const a: string = "start:"; return a.concat(true, ":", false); }""", 4, "start:true:false")]
    [InlineData("undef", """export function main(): string { const a: string = "start:"; return a.concat(undefined); }""", 2, "start:undefined")]
    [InlineData("dynamic", """export function main(): string { const a: string = "start:"; let v: any = undefined; return a.concat(v, ":", null, ":", false, ":", 42); }""", 8, "start:undefined:null:false:42")]
    [InlineData("explicit-null", """export function main(): string { const a: string = "start:"; return a.concat(null, ":", "end"); }""", 4, "start:null:end")]
    [InlineData("strings", """export function main(): string { const a: string = "start:"; return a.concat("x", ":", "end"); }""", 4, "start:x:end")]
    [InlineData("omit", """export function main(): string { const a: string = "start:"; return a.concat(); }""", 1, "start:")]
    public void StringConcat_ConvertsDirectBooleansAndUndefined(string row, string source, int argc, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        output.WriteLine($"String.concat row={row}; parser/binder={parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions,
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::concat");
        output.WriteLine($"String.concat row={row}; target={call.OperandObject}; argc={call.Operand1}");
        Assert.Equal(argc, call.Operand1);
        var before = instructions[..Array.IndexOf(instructions, call)];
        for (var i = 0; i < before.Length; i++)
        {
            var instruction = before[i];
            output.WriteLine($"String.concat row={row}; ir={i}; opcode={instruction.Opcode}; operand0={instruction.Operand0}; operand1={instruction.Operand1}; object={instruction.OperandObject}");
        }
        var arguments = before[^argc..];
        switch (row)
        {
            case "booleans":
                Assert.Equal(Opcode.LoadConst_Bool, arguments[1].Opcode);
                Assert.Equal(1, arguments[1].Operand0);
                Assert.Equal(Opcode.LoadConst_Bool, arguments[3].Opcode);
                Assert.Equal(0, arguments[3].Operand0);
                break;
            case "undef":
                Assert.Equal(Opcode.LoadConst_Void, arguments[1].Opcode);
                break;
            case "dynamic":
                Assert.Equal(Opcode.LoadLocal, arguments[1].Opcode);
                var slot = arguments[1].Operand0;
                var store = Assert.Single(before, instruction =>
                    instruction.Opcode == Opcode.StoreLocal && instruction.Operand0 == slot);
                var storeIndex = Array.IndexOf(before, store);
                Assert.InRange(storeIndex, 1, before.Length - argc - 1);
                Assert.Equal(Opcode.LoadConst_Void, before[storeIndex - 1].Opcode);
                Assert.Equal(Opcode.LoadConst_Null, arguments[3].Opcode);
                Assert.Equal(Opcode.LoadConst_Bool, arguments[5].Opcode);
                Assert.Equal(0, arguments[5].Operand0);
                break;
            case "explicit-null":
                Assert.Equal(Opcode.LoadConst_Null, arguments[1].Opcode);
                break;
            case "omit":
                Assert.DoesNotContain(arguments, instruction => instruction.Opcode == Opcode.LoadConst_Void);
                break;
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"String.concat row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"String.concat row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsStringValue)?.Value}");
        var value = Assert.IsType<TsStringValue>(result);
        Assert.Equal(expected, value.Value);
    }
}
