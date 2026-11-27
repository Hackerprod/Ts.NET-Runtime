using System.Globalization;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class ArrayJoinInvariantFiniteElementsTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    public void JoinUsesInvariantFormattingForFiniteLiteralElements(string locale)
    {
        Assert.Equal("1.5,2.25", Execute(
            "function main(): string { return [1.5, 2.25].join(); }", locale, 1, []));
        Assert.Equal("1.5|2.25", Execute(
            "function main(): string { return [1.5, 2.25].join('|'); }", locale, 2, []));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    public void JoinPreservesNativePrecisionScaleAndOtherFields(string locale)
    {
        var backing = new TsArray();
        TsValue[] fields =
        [
            new TsFloat32Value(0.1f), new TsFloat64Value(-2.25), new TsDecimalValue(3.7500m),
            new TsFloat32Value(BitConverter.Int32BitsToSingle(int.MinValue)),
            new TsFloat64Value(BitConverter.Int64BitsToDouble(long.MinValue)),
            TsValue.Null, TsValue.Void, TsValue.Void, TsValue.FromBool(true), TsValue.FromString("1,2")
        ];
        const int hole = 7;
        for (int i = 0; i < fields.Length; i++)
        {
            if (i == hole) backing.SetHole(i);
            else backing.Add(fields[i]);
        }

        var receiver = new TsArrayValue(backing);
        TsValue[] args = [receiver];
        Assert.Equal("0.1|-2.25|3.7500|0|0||||true|1,2", Execute(
            "function main(a: any[]): string { return a.join('|'); }", locale, 2, args));

        Assert.Same(receiver, args[0]);
        Assert.Same(backing, receiver.Value);
        Assert.Equal(fields.Length, backing.Count);
        for (int i = 0; i < fields.Length; i++)
        {
            Assert.Equal(i == hole, backing.IsHole(i));
            Assert.Same(fields[i], backing.Get(i));
        }
    }

    private static string Execute(string source, string locale, int argc, TsValue[] args)
    {
        var parser = new Parser(new Lexer(source).Tokenize());
        var syntax = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::join", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

        var code = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(code);
        var vm = new Vm();
        var culture = CultureInfo.GetCultureInfo(locale);
        Assert.Equal(locale == "en-US" ? "." : ",", culture.NumberFormat.NumberDecimalSeparator);
        var previous = CultureInfo.CurrentCulture;
        TsValue? result;
        try
        {
            CultureInfo.CurrentCulture = culture;
            result = vm.Execute(code, "main", args);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        return Assert.IsType<TsStringValue>(result).Value;
    }
}
