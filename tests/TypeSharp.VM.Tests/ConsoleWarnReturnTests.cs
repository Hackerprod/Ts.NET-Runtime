using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleWarnReturnStreamsCollection
{
    public const string Name = "ConsoleWarnReturnTests stream capture";
}

[Collection(ConsoleWarnReturnStreamsCollection.Name)]
public sealed class ConsoleWarnReturnTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("warn-one", "warn", "threshold", null)]
    [InlineData("warn-two", "warn", "limit", "near")]
    [InlineData("log-control", "log", "ready", null)]
    public void ConsoleWarn_PrintsAndReturnsUndefined(string row, string method, string first, string? second)
    {
        string[] arguments = second is null ? [first] : [first, second];
        var literals = string.Join(", ", arguments.Select(argument => $"\"{argument}\""));
        var source = $"function main(): any {{ return console.{method}({literals}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        output.WriteLine($"row={row}; source={source}; parser/binder={parser.Diagnostics.Count}/{binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        var valueReturn = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Return);
        Assert.Equal($"console::{method}", target);
        Assert.Equal(arguments.Length, call.Operand1);
        Assert.Equal(arguments.Length + 2, instructions.Length);
        Assert.Equal(call, instructions[^2]);
        Assert.Equal(valueReturn, instructions[^1]);
        for (var i = 0; i < arguments.Length; i++)
        {
            Assert.Equal(Opcode.LoadConst_String, instructions[i].Opcode);
            Assert.Equal(arguments[i], Assert.IsType<string>(instructions[i].OperandObject));
        }
        output.WriteLine($"route=[{literals}] {call.Opcode} target={target} argc={call.Operand1}; Return");

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new StringWriter();
        using var capturedError = new StringWriter();
        TsValue? result = null;
        try
        {
            try
            {
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                result = vm.Execute(bytecode, "main");
            }
            finally
            {
                try { Console.SetOut(originalOut); }
                finally { Console.SetError(originalError); }
            }
        }
        catch (Exception exception)
        {
            output.WriteLine($"exception={exception}");
            throw;
        }

        var stdout = capturedOut.ToString();
        var stderr = capturedError.ToString();
        var isVoid = ReferenceEquals(TsValue.Void, result);
        var isNull = ReferenceEquals(TsValue.Null, result);
        var observed = $"{result?.GetType().Name ?? "CLR null"}/{result?.ValueType.ToString() ?? "none"}; identity:void={isVoid}; identity:null={isNull}";
        output.WriteLine($"stdout=\"{Escape(stdout)}\"; stderr=\"{Escape(stderr)}\"; expected=TsVoid/Void singleton; actual={observed}; exception=none");
        var line = string.Join(" ", arguments) + Environment.NewLine;
        Assert.Equal(method == "log" ? line : "", stdout);
        Assert.Equal(method == "warn" ? line : "", stderr);
        if (method == "warn")
            Assert.True(isVoid, $"C126_CONSOLE_WARN_RETURN_MISMATCH row={row}; expected=TsVoid/Void singleton; actual={observed}");
        else
            Assert.Same(TsValue.Void, result);
    }

    private static string Escape(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");
}
