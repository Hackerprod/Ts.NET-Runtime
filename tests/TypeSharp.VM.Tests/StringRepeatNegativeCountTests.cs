using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringRepeatNegativeCountTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("negative-integer", "x", -1, null)]
    [InlineData("negative-fraction-empty-receiver", "", -1.9, null)]
    [InlineData("negative-fraction-truncates-zero", "xyz", -0.9, "")]
    [InlineData("negative-zero", "ab", -0.0, "")]
    [InlineData("explicit-zero", "abc", 0, "")]
    [InlineData("positive-fraction", "xy", 2.9, "xyxy")]
    public void StringRepeat_FiniteNumberAfterTruncation(string row, string receiver, object count, string? expected)
    {
        const string source = "function main(s: string, n: number): any { try { return s.repeat(n); } catch (e) { return e; } }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var callBlock = Assert.Single(main.Blocks, block => block.Instructions.Contains(call));
        int callIndex = callBlock.Instructions.IndexOf(call);
        var receiverLoad = callBlock.Instructions[callIndex - 2];
        var countLoad = callBlock.Instructions[callIndex - 1];
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::repeat", target);
        Assert.Equal(2, call.Operand1);
        Assert.Equal(Opcode.LoadArg, receiverLoad.Opcode);
        Assert.Equal(0, receiverLoad.Operand0);
        Assert.Equal(Opcode.LoadArg, countLoad.Opcode);
        Assert.Equal(1, countLoad.Operand0);
        var enterTry = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.EnterTry);
        Assert.True(callBlock.Instructions.IndexOf(enterTry) >= 0 &&
            callBlock.Instructions.IndexOf(enterTry) < callIndex - 2);
        var handler = Assert.Single(main.Blocks, block => block.Id == enterTry.Operand0);
        Assert.Equal(new[] { Opcode.StoreLocal, Opcode.LoadLocal, Opcode.Return },
            handler.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(handler.Instructions[0].Operand0, handler.Instructions[1].Operand0);

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue[] args = [TsValue.FromString(receiver), count is int i ? TsValue.FromInt32(i) : TsValue.FromFloat64(Assert.IsType<double>(count))];
        var input = Assert.IsType<TsStringValue>(args[0]);
        Assert.Equal(receiver, input.Value);
        string countValue;
        string? countBits = null;
        if (count is int integer)
        {
            int actualCount = Assert.IsType<TsInt32Value>(args[1]).Value;
            Assert.Equal(integer, actualCount);
            countValue = actualCount.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            double actualCount = Assert.IsType<TsFloat64Value>(args[1]).Value;
            long bits = BitConverter.DoubleToInt64Bits(actualCount);
            Assert.Equal(BitConverter.DoubleToInt64Bits(Assert.IsType<double>(count)), bits);
            if (row == "negative-zero")
                Assert.Equal(long.MinValue, bits);
            countValue = actualCount.ToString("R", CultureInfo.InvariantCulture);
            countBits = bits.ToString("X16", CultureInfo.InvariantCulture);
        }

        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var actualString = (result as TsStringValue)?.Value;
        var actualObject = (result as TsObjectValue)?.Value;
        var name = actualObject?.Fields.GetValueOrDefault("name");
        var message = actualObject?.Fields.GetValueOrDefault("message");
        var nameText = (name as TsStringValue)?.Value;
        var messageText = (message as TsStringValue)?.Value;
        output.WriteLine("String.repeat finite " + JsonSerializer.Serialize(new
        {
            phase = "finite-number-after-truncation", row, parserCount, binderCount, target, argc = call.Operand1,
            receiverOp = receiverLoad.Opcode.ToString(), receiverArg = receiverLoad.Operand0,
            countOp = countLoad.Opcode.ToString(), countArg = countLoad.Operand0,
            handlerOps = handler.Instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
            handlerStore = handler.Instructions[0].Operand0, handlerLoad = handler.Instructions[1].Operand0,
            receiver = input.Value, receiverType = input.GetType().Name, countType = args[1].GetType().Name,
            countValue, countBits, expectedType = expected is null ? nameof(TsObjectValue) : nameof(TsStringValue),
            expectedString = expected, expectedErrorName = expected is null ? "RangeError" : null,
            actualType = result?.GetType().Name, actualString, actualTypeName = actualObject?.TypeName,
            nameType = name?.GetType().Name, nameText, messageType = message?.GetType().Name, messageText,
            escaped = escaped?.GetType().FullName
        }));

        Assert.Null(escaped);
        Assert.True(expected is null
            ? actualObject?.TypeName == "RangeError" && nameText == "RangeError" && !string.IsNullOrEmpty(messageText)
            : result is TsStringValue && actualString == expected,
            $"String.repeat final result disagrees for {row}");
    }
}
