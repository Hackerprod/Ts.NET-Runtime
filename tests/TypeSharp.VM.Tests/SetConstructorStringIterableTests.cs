using System.Globalization;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class SetConstructorStringIterableTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bmp-repeat", "\"banana\"", 1, "0062,0061,006E", true)]
    [InlineData("supplementary-repeat", "\"💡a💡\"", 1, "D83D+DCA1,0061", true)]
    [InlineData("empty-string", "\"\"", 1, "", true)]
    [InlineData("array-control", "[\"b\",\"a\",\"b\"]", 1, "0062,0061", false)]
    [InlineData("default-control", "", 0, "", false)]
    public void ConstructSet_PreservesOrderedUniqueStrings(
        string row, string arguments, int constructorArity, string expectedEntries, bool stringInput)
    {
        var source = $"function main(): Set<string> {{ return new Set<string>({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var constructor = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.NewObject);
        Assert.Equal("Set", Assert.IsType<string>(constructor.OperandObject));
        Assert.Equal(constructorArity, constructor.Operand1);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is
            Opcode.Call or Opcode.CallDynamic or Opcode.CallDynamicArray or Opcode.CallVirt);

        var route = $"C121 row={row}; arguments={arguments}; parser/binder=0/0; NewObject Set count=1 argc={constructorArity}; Call count=0";
        output.WriteLine(route);
        var bytecode = BytecodeCompiler.Compile(ir);
        TsValue result;
        try
        {
            result = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        }
        catch (InvalidOperationException exception) when (
            stringInput && exception.GetType() == typeof(InvalidOperationException) &&
            exception.Message == "Set constructor expects an array of values" &&
            exception.TargetSite?.Name == "PopulateSet" &&
            exception.TargetSite.DeclaringType == typeof(TypeSharp.VM.Interpreter.Interpreter))
        {
            var evidence = $"C121_SET_STRING_CONSTRUCTOR_UNSUPPORTED row={row}; native={exception.GetType().FullName}; origin=PopulateSet; message={exception.Message}; expected=[{expectedEntries}]";
            output.WriteLine(evidence);
            Assert.True(false, evidence);
            throw;
        }

        var set = Assert.IsType<TsSetValue>(result);
        var actual = set.Value.Entries.Select(entry => Encode(Assert.IsType<TsStringValue>(entry).Value)).ToArray();
        var expected = expectedEntries.Length == 0 ? [] : expectedEntries.Split(',');
        output.WriteLine($"C121 row={row}; tag=TsSetValue; entryTag=TsStringValue; count={actual.Length}; actual=[{string.Join(",", actual)}]; expected=[{expectedEntries}]");
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual, StringComparer.Ordinal);
    }

    // Preserve UTF-16 units inside each entry, without collapsing entry boundaries.
    private static string Encode(string value) =>
        string.Join("+", value.Select(unit => ((int)unit).ToString("X4", CultureInfo.InvariantCulture)));
}
