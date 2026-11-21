using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleLogReturnStdoutCollection
{
    public const string Name = "ConsoleLogReturnTests stdout capture";
}

[Collection(ConsoleLogReturnStdoutCollection.Name)]
public sealed class ConsoleLogReturnTests(ITestOutputHelper output)
{
    [Fact]
    public void ConsoleLog_PrintsAndReturnsUndefined()
    {
        const string source = """function main(): any { return console.log("hello"); }""";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("console::log", target);
        Assert.Equal(1, call.Operand1);
        Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Return);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == Opcode.ReturnVoid);

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        using var captured = new StringWriter();
        var originalOutput = Console.Out;
        TsValue? result;
        try
        {
            Console.SetOut(captured);
            result = vm.Execute(bytecode, "main");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var stdout = captured.ToString();
        var escapedStdout = stdout.Replace("\r", "\\r").Replace("\n", "\\n");
        output.WriteLine($"parser/binder=0/0; {call.Opcode} target={target} argc={call.Operand1}; Return; stdout=\"{escapedStdout}\"; expected={TsValue.Void.GetType().Name}; actual={result?.GetType().Name ?? "CLR null"}");
        Assert.Equal("hello" + Environment.NewLine, stdout);
        Assert.Same(TsValue.Void, result);
    }
}
