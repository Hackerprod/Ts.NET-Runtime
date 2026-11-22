using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringRepeatNativeNegativeNumberTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("float64-negative-infinity")]
    [InlineData("float64-below-int32")]
    [InlineData("float32-negative-infinity")]
    [InlineData("float32-below-int32")]
    [InlineData("float32-int32-min")]
    [InlineData("float32-fraction-toward-zero")]
    [InlineData("float64-nan")]
    public void StringRepeat_PreservesNegativeNumberRangeError(string row)
    {
        const string source = "function main(s: string, n: number): any { try { return s.repeat(n); } catch (e) { return e; } }";
        const string errorName = "RangeError";
        const string errorMessage = "String.repeat count must not be negative";

        (string receiver, object number, bool expectsError) = row switch
        {
            "float64-negative-infinity" => ("ab", double.NegativeInfinity, true),
            "float64-below-int32" => ("", -2147483649d, true),
            "float32-negative-infinity" => ("ab", (object)float.NegativeInfinity, true),
            "float32-below-int32" => ("ab", (object)-2147483904f, true),
            "float32-int32-min" => ("ab", (object)-2147483648f, true),
            "float32-fraction-toward-zero" => ("ab", (object)-0.9f, false),
            "float64-nan" => ("ab", double.NaN, false),
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };

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
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::repeat", target);
        Assert.Equal(2, call.Operand1);
        var callBlock = Assert.Single(main.Blocks, block => block.Instructions.Contains(call));
        int callIndex = callBlock.Instructions.IndexOf(call);
        var receiverLoad = callBlock.Instructions[callIndex - 2];
        var countLoad = callBlock.Instructions[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, receiverLoad.Opcode);
        Assert.Equal(0, receiverLoad.Operand0);
        Assert.Equal(Opcode.LoadArg, countLoad.Opcode);
        Assert.Equal(1, countLoad.Operand0);
        var enterTry = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.EnterTry);
        int tryIndex = callBlock.Instructions.IndexOf(enterTry);
        bool enterTryProtectsCall = tryIndex >= 0 && tryIndex < callIndex - 2;
        Assert.True(enterTryProtectsCall);
        var handler = Assert.Single(main.Blocks, block => block.Id == enterTry.Operand0);
        Assert.Equal(new[] { Opcode.StoreLocal, Opcode.LoadLocal, Opcode.Return },
            handler.Instructions.Select(instruction => instruction.Opcode));
        int handlerStore = handler.Instructions[0].Operand0;
        int handlerLoad = handler.Instructions[1].Operand0;
        Assert.Equal(handlerStore, handlerLoad);

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var input = Assert.IsType<TsStringValue>(TsValue.FromString(receiver));
        Assert.Equal(receiver, input.Value);
        TsValue count;
        string countValue;
        string countBits;
        if (number is float single)
        {
            var native = Assert.IsType<TsFloat32Value>(TsValue.FromFloat32(single));
            int bits = BitConverter.SingleToInt32Bits(native.Value);
            Assert.Equal(BitConverter.SingleToInt32Bits(single), bits);
            count = native;
            countValue = native.Value.ToString("R", CultureInfo.InvariantCulture);
            countBits = bits.ToString("X8", CultureInfo.InvariantCulture);
        }
        else
        {
            double wide = Assert.IsType<double>(number);
            var native = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(wide));
            long bits = BitConverter.DoubleToInt64Bits(native.Value);
            Assert.Equal(BitConverter.DoubleToInt64Bits(wide), bits);
            count = native;
            countValue = native.Value.ToString("R", CultureInfo.InvariantCulture);
            countBits = bits.ToString("X16", CultureInfo.InvariantCulture);
        }

        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", [input, count]); });
        var actualString = (result as TsStringValue)?.Value;
        var actualObject = (result as TsObjectValue)?.Value;
        var name = actualObject?.Fields.GetValueOrDefault("name");
        var message = actualObject?.Fields.GetValueOrDefault("message");
        var nameText = (name as TsStringValue)?.Value;
        var messageText = (message as TsStringValue)?.Value;
        output.WriteLine("C162 String.repeat native number " + JsonSerializer.Serialize(new
        {
            phase = "native-negative-number", row, parserCount, binderCount, target, argc = call.Operand1,
            receiverOp = receiverLoad.Opcode.ToString(), receiverArg = receiverLoad.Operand0,
            countOp = countLoad.Opcode.ToString(), countArg = countLoad.Operand0, enterTryProtectsCall,
            handlerOps = handler.Instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
            handlerStore, handlerLoad, receiver = input.Value, receiverType = input.GetType().Name,
            countType = count.GetType().Name, countValue, countBits,
            expectedType = expectsError ? nameof(TsObjectValue) : nameof(TsStringValue),
            expectedString = expectsError ? null : "",
            expectedErrorName = expectsError ? errorName : null,
            expectedErrorMessage = expectsError ? errorMessage : null,
            actualType = result?.GetType().Name, actualString, actualTypeName = actualObject?.TypeName,
            nameType = name?.GetType().Name, nameText, messageType = message?.GetType().Name, messageText,
            escaped = escaped?.GetType().FullName
        }));

        Assert.Null(escaped);
        Assert.True(expectsError
            ? result is TsObjectValue && actualObject?.TypeName == errorName &&
                name is TsStringValue && nameText == errorName &&
                message is TsStringValue && messageText == errorMessage
            : result is TsStringValue && actualString == "",
            $"String.repeat native negative Number final result disagrees for {row}");
    }
}
