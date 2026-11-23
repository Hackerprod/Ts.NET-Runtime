using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringRepeatNegativeTextTests(ITestOutputHelper output)
{
    [Fact]
    public void StringRepeat_NumericTextAfterTruncation()
    {
        const string source = "function main(s: string, n: any): any { try { return s.repeat(n); } catch (e) { return e; } }";
        const string errorName = "RangeError";
        const string errorMessage = "String.repeat count must not be negative";
        var tokens = new Lexer(source).Tokenize();
        int lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Equal(0, lexerCount);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var callBlock = Assert.Single(main.Blocks, block => block.Instructions.Contains(call));
        int callIndex = callBlock.Instructions.IndexOf(call);
        Assert.True(callIndex >= 2);
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
        int enterTryIndex = callBlock.Instructions.IndexOf(enterTry);
        Assert.True(enterTryIndex >= 0 && enterTryIndex < callIndex - 2);
        var handler = Assert.Single(main.Blocks, block => block.Id == enterTry.Operand0);
        Assert.Equal(new[] { Opcode.StoreLocal, Opcode.LoadLocal, Opcode.Return },
            handler.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(handler.Instructions[0].Operand0, handler.Instructions[1].Operand0);

        var bytecode = BytecodeCompiler.Compile(ir);
        (string Row, string Count, string? Expected)[] rows =
        [
            ("negative-decimal-text", "-1.9", null),
            ("negative-fraction-text-truncates-zero", "-0.9", ""),
            ("positive-decimal-text", "2.9", "abab")
        ];
        var mismatches = new List<string>();
        foreach (var (row, count, expected) in rows)
        {
            var vm = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue[] args = [TsValue.FromString("ab"), TsValue.FromString(count)];
            var receiverInput = Assert.IsType<TsStringValue>(args[0]);
            var countInput = Assert.IsType<TsStringValue>(args[1]);
            Assert.Equal("ab", receiverInput.Value);
            Assert.Equal(count, countInput.Value);

            TsValue? result = null;
            var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
            var actualString = (result as TsStringValue)?.Value;
            var actualObject = (result as TsObjectValue)?.Value;
            var name = actualObject?.Fields.GetValueOrDefault("name");
            var message = actualObject?.Fields.GetValueOrDefault("message");
            var nameText = (name as TsStringValue)?.Value;
            var messageText = (message as TsStringValue)?.Value;
            output.WriteLine("C181 String.repeat text " + JsonSerializer.Serialize(new
            {
                row, source, lexerCount, parserCount, binderCount, target, argc = call.Operand1,
                receiverOp = receiverLoad.Opcode.ToString(), receiverArg = receiverLoad.Operand0,
                countOp = countLoad.Opcode.ToString(), countArg = countLoad.Operand0,
                callIndex, enterTryIndex, handlerBlock = handler.Id,
                handlerOps = handler.Instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
                handlerStore = handler.Instructions[0].Operand0, handlerLoad = handler.Instructions[1].Operand0,
                receiver = receiverInput.Value, receiverType = receiverInput.GetType().Name,
                countValue = countInput.Value, countType = countInput.GetType().Name,
                actualType = result?.GetType().Name, actualString, actualTypeName = actualObject?.TypeName,
                nameType = name?.GetType().Name, nameText, messageType = message?.GetType().Name, messageText,
                escaped = escaped?.GetType().FullName,
                expectedType = expected is null ? nameof(TsObjectValue) : nameof(TsStringValue),
                expectedString = expected, expectedErrorName = expected is null ? errorName : null,
                expectedErrorMessage = expected is null ? errorMessage : null
            }));

            bool matches = escaped is null && (expected is null
                ? result is TsObjectValue && actualObject?.TypeName == errorName &&
                    name is TsStringValue && nameText == errorName &&
                    message is TsStringValue && messageText == errorMessage
                : result is TsStringValue && actualString == expected);
            if (!matches)
                mismatches.Add(row);
        }

        if (mismatches.Count != 0)
            Assert.Fail("C181_SEMANTIC_MISMATCHES " + string.Join(",", mismatches));
    }
}
