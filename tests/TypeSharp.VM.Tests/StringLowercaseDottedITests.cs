using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringLowercaseDottedITests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("standalone", "\u0130", "\u0069\u0307")]
    [InlineData("word", "\u0130STANBUL", "i\u0307stanbul")]
    [InlineData("repeated-and-supplementary", "A\u0130\U00010400\u0130Z", "ai\u0307\U00010428i\u0307z")]
    [InlineData("decomposed", "I\u0307STANBUL", "i\u0307stanbul")]
    [InlineData("ordinary", "I \u00C9 \u00C7!", "i \u00E9 \u00E7!")]
    [InlineData("empty", "", "")]
    public void StringToLowerCase_DefaultDottedIExpansion(string row, string text, string expected)
    {
        static string Units(string text) =>
            string.Join(" ", text.Select(unit => ((ushort)unit).ToString("X4", CultureInfo.InvariantCulture)));

        const string source = "function main(s: string): string { return s.toLowerCase(); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = Assert.Single(main.Blocks).Instructions;
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.Call, Opcode.Return },
            instructions.Select(instruction => instruction.Opcode));
        var receiver = instructions[0];
        var call = instructions[1];
        string target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::toLowerCase", target);
        Assert.Equal(1, call.Operand1);
        Assert.Equal(0, receiver.Operand0);

        var input = new TsStringValue(text);
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", new TsValue[] { input }); });
        var actual = (result as TsStringValue)?.Value;
        string expectedUnits = Units(expected);
        string? actualUnits = actual is null ? null : Units(actual);
        output.WriteLine("C109 lowercase row " + JsonSerializer.Serialize(new
        {
            row, parserCount, binderCount, target, argc = call.Operand1,
            ir = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
            receiverOp = receiver.Opcode.ToString(), receiverArg = receiver.Operand0,
            inputType = input.GetType().Name, input = Units(input.Value),
            actualType = result?.GetType().Name, actual = actualUnits, expected = expectedUnits,
            escaped = escaped?.GetType().FullName
        }));
        Assert.Null(escaped);
        var value = Assert.IsType<TsStringValue>(result);
        Assert.True(string.Equals(expected, value.Value, StringComparison.Ordinal),
            $"C109 lowercase outcome disagrees for {row}: expected [{expectedUnits}]; actual [{actualUnits}]");
    }
}
