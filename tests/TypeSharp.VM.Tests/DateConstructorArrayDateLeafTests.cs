using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class DateConstructorArrayDateLeafTests
{
    private sealed record Row(string Name, string Source, double[] Expected, string[] DateRoutes);

    [Fact]
    public void NativeDateArrayLeaves_ProjectWholeUtcSecondsWithoutChangingCopiesOrNumberComponents()
    {
        // Original independent failing witness and its frozen companion rows.
        Row[] rows =
        [
            new("d-native-leaf-scope-probe",
                """function main(): any { const original: Date=new Date(1234); const wrapped: Date=new Date([original]); return [wrapped.getTime(),new Date([[original]]).valueOf()]; }""",
                [1000d, 1000d],
                ["Date/1", "Date/1", "Date::getTime/1", "Date/1", "Date::valueOf/1"]),
            new("leaf-negative-floor-and-direct-copies",
                """function main(): any { const a: Date = new Date(-1); const b: Date = new Date(-1001); return [new Date([a]).getTime(),new Date([[b]]).valueOf(),new Date(a).getTime(),new Date(b).valueOf()]; }""",
                [-1000d, -2000d, -1d, -1001d],
                ["Date/1", "Date/1", "Date/1", "Date::getTime/1", "Date/1", "Date::valueOf/1", "Date/1", "Date::getTime/1", "Date/1", "Date::valueOf/1"]),
            new("leaf-whole-seconds-and-positive-zero",
                """function main(): any { return [new Date([new Date(-1000)]).getTime(),new Date([[new Date(999)]]).valueOf(),new Date([new Date(2000)]).getTime()]; }""",
                [-1000d, 0d, 2000d],
                ["Date/1", "Date/1", "Date::getTime/1", "Date/1", "Date/1", "Date::valueOf/1", "Date/1", "Date/1", "Date::getTime/1"]),
            new("leaf-invalid-and-impostor-controls",
                """function main(): any { const bad: Date = new Date("not a date"); const fake = {__timestampMs:1234.5}; return [new Date([bad]).getTime(),new Date([[bad]]).valueOf(),new Date(bad).getTime(),new Date([fake]).getTime(),new Date(fake).getTime()]; }""",
                [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN],
                ["Date/1", "Date/1", "Date::getTime/1", "Date/1", "Date::valueOf/1", "Date/1", "Date::getTime/1", "Date/1", "Date::getTime/1", "Date/1", "Date::getTime/1"]),
            new("leaf-component-and-nested-numeric-year-controls",
                """function main(): any { const d: Date = new Date(2); return [new Date(1970,0,d).getTime(),new Date(1970,0,[d]).getTime(),new Date([[2024]]).getTime()]; }""",
                [86400000d, double.NaN, 1704067200000d],
                ["Date/1", "Date/3", "Date::getTime/1", "Date/3", "Date::getTime/1", "Date/1", "Date::getTime/1"]),
        ];

        foreach (var row in rows)
        {
            var tokens = new Lexer(row.Source).Tokenize();
            var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
            var binder = new TypeSharp.Semantics.Binder.Binder();
            var bound = binder.Bind(parser.Parse());
            Assert.True(tokens.All(token => token.Kind != TokenKind.Error), row.Name);
            Assert.Empty(parser.Diagnostics);
            Assert.Empty(binder.Diagnostics.All);

            var ir = new IRGenerator().Generate(bound);
            var entry = Assert.Single(ir.Functions, f => f.Name == "main" && f.Parameters.Count == 0);
            var dateRoutes = entry.Blocks.SelectMany(block => block.Instructions)
                .Where(i => i.Opcode == Opcode.NewObject && i.OperandObject is "Date" ||
                    i.Opcode == Opcode.Call && i.OperandObject is string target && target.StartsWith("Date::", StringComparison.Ordinal))
                .Select(i => $"{i.OperandObject}/{i.Operand1}").ToArray();
            Assert.Equal(row.DateRoutes, dateRoutes);

            var bytecode = BytecodeCompiler.Compile(ir);
            BytecodeVerifier.Verify(bytecode);
            var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
            var values = Assert.IsType<TsArrayValue>(result).Value;
            Assert.Equal(row.Expected.Length, values.Count);
            for (int i = 0; i < row.Expected.Length; i++)
            {
                var number = Assert.IsType<TsFloat64Value>(values.Get(i));
                double expected = row.Expected[i];
                Assert.True(double.IsNaN(expected)
                    ? double.IsNaN(number.Value)
                    : BitConverter.DoubleToInt64Bits(number.Value) == BitConverter.DoubleToInt64Bits(expected),
                    $"{row.Name}, element {i}: expected {expected}, received {number.Value}");
            }
        }
    }
}
