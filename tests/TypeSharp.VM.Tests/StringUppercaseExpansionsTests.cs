using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringUppercaseExpansionsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("U+00DF", "\u00DF", "\u0053\u0053", false)]
    [InlineData("U+0149", "\u0149", "\u02BC\u004E", false)]
    [InlineData("U+01F0", "\u01F0", "\u004A\u030C", false)]
    [InlineData("U+0390", "\u0390", "\u0399\u0308\u0301", false)]
    [InlineData("U+03B0", "\u03B0", "\u03A5\u0308\u0301", false)]
    [InlineData("U+0587", "\u0587", "\u0535\u0552", false)]
    [InlineData("U+1E96", "\u1E96", "\u0048\u0331", false)]
    [InlineData("U+1E97", "\u1E97", "\u0054\u0308", false)]
    [InlineData("U+1E98", "\u1E98", "\u0057\u030A", false)]
    [InlineData("U+1E99", "\u1E99", "\u0059\u030A", false)]
    [InlineData("U+1E9A", "\u1E9A", "\u0041\u02BE", false)]
    [InlineData("U+1F50", "\u1F50", "\u03A5\u0313", false)]
    [InlineData("U+1F52", "\u1F52", "\u03A5\u0313\u0300", false)]
    [InlineData("U+1F54", "\u1F54", "\u03A5\u0313\u0301", false)]
    [InlineData("U+1F56", "\u1F56", "\u03A5\u0313\u0342", false)]
    [InlineData("U+1F80", "\u1F80", "\u1F08\u0399", false)]
    [InlineData("U+1F81", "\u1F81", "\u1F09\u0399", false)]
    [InlineData("U+1F82", "\u1F82", "\u1F0A\u0399", false)]
    [InlineData("U+1F83", "\u1F83", "\u1F0B\u0399", false)]
    [InlineData("U+1F84", "\u1F84", "\u1F0C\u0399", false)]
    [InlineData("U+1F85", "\u1F85", "\u1F0D\u0399", false)]
    [InlineData("U+1F86", "\u1F86", "\u1F0E\u0399", false)]
    [InlineData("U+1F87", "\u1F87", "\u1F0F\u0399", false)]
    [InlineData("U+1F88", "\u1F88", "\u1F08\u0399", false)]
    [InlineData("U+1F89", "\u1F89", "\u1F09\u0399", false)]
    [InlineData("U+1F8A", "\u1F8A", "\u1F0A\u0399", false)]
    [InlineData("U+1F8B", "\u1F8B", "\u1F0B\u0399", false)]
    [InlineData("U+1F8C", "\u1F8C", "\u1F0C\u0399", false)]
    [InlineData("U+1F8D", "\u1F8D", "\u1F0D\u0399", false)]
    [InlineData("U+1F8E", "\u1F8E", "\u1F0E\u0399", false)]
    [InlineData("U+1F8F", "\u1F8F", "\u1F0F\u0399", false)]
    [InlineData("U+1F90", "\u1F90", "\u1F28\u0399", false)]
    [InlineData("U+1F91", "\u1F91", "\u1F29\u0399", false)]
    [InlineData("U+1F92", "\u1F92", "\u1F2A\u0399", false)]
    [InlineData("U+1F93", "\u1F93", "\u1F2B\u0399", false)]
    [InlineData("U+1F94", "\u1F94", "\u1F2C\u0399", false)]
    [InlineData("U+1F95", "\u1F95", "\u1F2D\u0399", false)]
    [InlineData("U+1F96", "\u1F96", "\u1F2E\u0399", false)]
    [InlineData("U+1F97", "\u1F97", "\u1F2F\u0399", false)]
    [InlineData("U+1F98", "\u1F98", "\u1F28\u0399", false)]
    [InlineData("U+1F99", "\u1F99", "\u1F29\u0399", false)]
    [InlineData("U+1F9A", "\u1F9A", "\u1F2A\u0399", false)]
    [InlineData("U+1F9B", "\u1F9B", "\u1F2B\u0399", false)]
    [InlineData("U+1F9C", "\u1F9C", "\u1F2C\u0399", false)]
    [InlineData("U+1F9D", "\u1F9D", "\u1F2D\u0399", false)]
    [InlineData("U+1F9E", "\u1F9E", "\u1F2E\u0399", false)]
    [InlineData("U+1F9F", "\u1F9F", "\u1F2F\u0399", false)]
    [InlineData("U+1FA0", "\u1FA0", "\u1F68\u0399", false)]
    [InlineData("U+1FA1", "\u1FA1", "\u1F69\u0399", false)]
    [InlineData("U+1FA2", "\u1FA2", "\u1F6A\u0399", false)]
    [InlineData("U+1FA3", "\u1FA3", "\u1F6B\u0399", false)]
    [InlineData("U+1FA4", "\u1FA4", "\u1F6C\u0399", false)]
    [InlineData("U+1FA5", "\u1FA5", "\u1F6D\u0399", false)]
    [InlineData("U+1FA6", "\u1FA6", "\u1F6E\u0399", false)]
    [InlineData("U+1FA7", "\u1FA7", "\u1F6F\u0399", false)]
    [InlineData("U+1FA8", "\u1FA8", "\u1F68\u0399", false)]
    [InlineData("U+1FA9", "\u1FA9", "\u1F69\u0399", false)]
    [InlineData("U+1FAA", "\u1FAA", "\u1F6A\u0399", false)]
    [InlineData("U+1FAB", "\u1FAB", "\u1F6B\u0399", false)]
    [InlineData("U+1FAC", "\u1FAC", "\u1F6C\u0399", false)]
    [InlineData("U+1FAD", "\u1FAD", "\u1F6D\u0399", false)]
    [InlineData("U+1FAE", "\u1FAE", "\u1F6E\u0399", false)]
    [InlineData("U+1FAF", "\u1FAF", "\u1F6F\u0399", false)]
    [InlineData("U+1FB2", "\u1FB2", "\u1FBA\u0399", false)]
    [InlineData("U+1FB3", "\u1FB3", "\u0391\u0399", false)]
    [InlineData("U+1FB4", "\u1FB4", "\u0386\u0399", false)]
    [InlineData("U+1FB6", "\u1FB6", "\u0391\u0342", false)]
    [InlineData("U+1FB7", "\u1FB7", "\u0391\u0342\u0399", false)]
    [InlineData("U+1FBC", "\u1FBC", "\u0391\u0399", false)]
    [InlineData("U+1FC2", "\u1FC2", "\u1FCA\u0399", false)]
    [InlineData("U+1FC3", "\u1FC3", "\u0397\u0399", false)]
    [InlineData("U+1FC4", "\u1FC4", "\u0389\u0399", false)]
    [InlineData("U+1FC6", "\u1FC6", "\u0397\u0342", false)]
    [InlineData("U+1FC7", "\u1FC7", "\u0397\u0342\u0399", false)]
    [InlineData("U+1FCC", "\u1FCC", "\u0397\u0399", false)]
    [InlineData("U+1FD2", "\u1FD2", "\u0399\u0308\u0300", false)]
    [InlineData("U+1FD3", "\u1FD3", "\u0399\u0308\u0301", false)]
    [InlineData("U+1FD6", "\u1FD6", "\u0399\u0342", false)]
    [InlineData("U+1FD7", "\u1FD7", "\u0399\u0308\u0342", false)]
    [InlineData("U+1FE2", "\u1FE2", "\u03A5\u0308\u0300", false)]
    [InlineData("U+1FE3", "\u1FE3", "\u03A5\u0308\u0301", false)]
    [InlineData("U+1FE4", "\u1FE4", "\u03A1\u0313", false)]
    [InlineData("U+1FE6", "\u1FE6", "\u03A5\u0342", false)]
    [InlineData("U+1FE7", "\u1FE7", "\u03A5\u0308\u0342", false)]
    [InlineData("U+1FF2", "\u1FF2", "\u1FFA\u0399", false)]
    [InlineData("U+1FF3", "\u1FF3", "\u03A9\u0399", false)]
    [InlineData("U+1FF4", "\u1FF4", "\u038F\u0399", false)]
    [InlineData("U+1FF6", "\u1FF6", "\u03A9\u0342", false)]
    [InlineData("U+1FF7", "\u1FF7", "\u03A9\u0342\u0399", false)]
    [InlineData("U+1FFC", "\u1FFC", "\u03A9\u0399", false)]
    [InlineData("U+FB00", "\uFB00", "\u0046\u0046", false)]
    [InlineData("U+FB01", "\uFB01", "\u0046\u0049", false)]
    [InlineData("U+FB02", "\uFB02", "\u0046\u004C", false)]
    [InlineData("U+FB03", "\uFB03", "\u0046\u0046\u0049", false)]
    [InlineData("U+FB04", "\uFB04", "\u0046\u0046\u004C", false)]
    [InlineData("U+FB05", "\uFB05", "\u0053\u0054", false)]
    [InlineData("U+FB06", "\uFB06", "\u0053\u0054", false)]
    [InlineData("U+FB13", "\uFB13", "\u0544\u0546", false)]
    [InlineData("U+FB14", "\uFB14", "\u0544\u0535", false)]
    [InlineData("U+FB15", "\uFB15", "\u0544\u053B", false)]
    [InlineData("U+FB16", "\uFB16", "\u054E\u0546", false)]
    [InlineData("U+FB17", "\uFB17", "\u0544\u053D", false)]
    [InlineData("word", "\u0073\u0074\u0072\u0061\u00DF\u0065", "\u0053\u0054\u0052\u0041\u0053\u0053\u0045", false)]
    [InlineData("consecutive", "\u0061\u00DF\uFB03\u1FB3\u007A", "\u0041\u0053\u0053\u0046\u0046\u0049\u0391\u0399\u005A", false)]
    [InlineData("split-supplementary", "\uD801\uDC28\u00DF\uD83D\uDE00\u007A", "\uD801\uDC00\u0053\u0053\uD83D\uDE00\u005A", false)]
    [InlineData("edge-expansions", "\u00DF\uD801\uDC28\uD801\uDC4F\uFB03", "\u0053\u0053\uD801\uDC00\uD801\uDC27\u0046\u0046\u0049", false)]
    [InlineData("malformed-mixed", "0061 D800 00DF DC00 007A", "0041 D800 0053 0053 DC00 005A", true)]
    [InlineData("empty", "", "", false)]
    [InlineData("ascii", "\u0061\u005A\u0030\u0021", "\u0041\u005A\u0030\u0021", false)]
    [InlineData("simple-bmp", "\u0061\u00E9\u03C3\u212A", "\u0041\u00C9\u03A3\u212A", false)]
    [InlineData("dotless-simple", "\u0131\u00E9", "\u0131\u00C9", false)]
    [InlineData("deseret", "\uD801\uDC28", "\uD801\uDC00", false)]
    [InlineData("emoji", "\uD83D\uDE00", "\uD83D\uDE00", false)]
    [InlineData("malformed-unmatched", "0061 D800 D800 007A DC00", "0041 D800 D800 005A DC00", true)]
    public void StringToUpperCase_DefaultExpansions(
        string row, string sourceText, string expectedText, bool isHex = false)
    {
        static string FromHex(string hex) =>
            new(hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(unit => (char)ushort.Parse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());

        static string Units(string text) =>
            string.Join(" ", text.Select(unit => ((ushort)unit).ToString("X4", CultureInfo.InvariantCulture)));

        string text = isHex ? FromHex(sourceText) : sourceText;
        string expected = isHex ? FromHex(expectedText) : expectedText;
        const string source = "function main(s: string): string { return s.toUpperCase(); }";
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
        Assert.Equal("String::toUpperCase", target);
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
        output.WriteLine("C94 uppercase row " + JsonSerializer.Serialize(new
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
            $"C94 uppercase outcome disagrees for {row}: expected [{expectedUnits}]; actual [{actualUnits}]");
    }
}
