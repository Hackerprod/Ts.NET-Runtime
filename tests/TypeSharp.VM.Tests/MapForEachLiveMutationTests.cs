using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MapForEachLiveMutationTests(ITestOutputHelper output)
{
    [Fact]
    public void MapForEach_LiveMutationPreservesVisitOrder()
    {
        const string source = """
            function visitInner(values: Map<string, int32>, visits: any[]): void {
                values.forEach((value: any, key: any) => {
                    visits.push("I");
                    visits.push(key);
                    visits.push(value);
                    if (key === "b") { values.delete("a"); }
                });
            }

            function main(values: Map<string, int32>, visits: any[], flags: any[], mode: any): void {
                values.forEach((value: any, key: any) => {
                    if (mode === "nested") { visits.push("O"); }
                    visits.push(key);
                    visits.push(value);
                    if (key === "a") {
                        if (mode === "delete-current") { values.delete("a"); }
                        if (mode === "delete-next") {
                            flags.push(values.delete("b"));
                            flags.push(values.delete("missing"));
                        }
                        if (mode === "clear-and-append") {
                            values.clear();
                            values.set("x", 8);
                            values.set("y", 9);
                        }
                        if (mode === "overwrite-and-append") {
                            values.set("b", 8);
                            values.set("c", 9);
                        }
                    }
                    if (key === "b") {
                        if (mode === "delete-earlier") { values.delete("a"); }
                        if (mode === "nested") { visitInner(values, visits); }
                    }
                });
            }
            """;

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var calls = ir.Functions.SelectMany(function => function.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Map::forEach")
            .Select(instruction => (Function: function.Name, Argc: instruction.Operand1))).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains(("main", 2), calls);
        Assert.Contains(("visitInner", 2), calls);
        output.WriteLine("Map.forEach route: parser/binder=0/0; main and visitInner each call Map::forEach at argc=2");
        var bytecode = BytecodeCompiler.Compile(ir);

        (string Name, string InitialKeys, string ExpectedVisits, string ExpectedEntries, string ExpectedFlags)[] rows =
        [
            ("delete-current", "abc", "a,1,b,2,c,3", "b,2,c,3", ""),
            ("delete-earlier", "abcd", "a,1,b,2,c,3,d,4", "b,2,c,3,d,4", ""),
            ("delete-next", "abcd", "a,1,c,3,d,4", "a,1,c,3,d,4", "true,false"),
            ("clear-and-append", "abc", "a,1,x,8,y,9", "x,8,y,9", ""),
            ("overwrite-and-append", "ab", "a,1,b,8,c,9", "a,1,b,8,c,9", ""),
            ("nested", "abcd", "O,a,1,O,b,2,I,a,1,I,b,2,I,c,3,I,d,4,O,c,3,O,d,4", "b,2,c,3,d,4", ""),
        ];

        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            var values = new TsMap();
            foreach (var key in row.InitialKeys)
                values.Set(TsValue.FromString(key.ToString()), TsValue.FromInt32(key - 'a' + 1));
            var visits = new TsArray();
            var flags = new TsArray();
            var vm = new TypeSharp.VM.Interpreter.Interpreter();
            vm.Execute(bytecode, "main",
                [new TsMapValue(values), new TsArrayValue(visits), new TsArrayValue(flags), TsValue.FromString(row.Name)]);

            var actualVisits = DescribeArray(visits);
            var actualEntries = string.Join(",", values.Entries.SelectMany(entry => new[] { Describe(entry.Key), Describe(entry.Value) }));
            var actualFlags = DescribeArray(flags);
            var passed = row.ExpectedVisits == actualVisits &&
                         row.ExpectedEntries == actualEntries &&
                         row.ExpectedFlags == actualFlags;
            var observation = $"{row.Name}: {(passed ? "PASS" : "MISMATCH")}; initialKeys={row.InitialKeys}; " +
                $"visits expected=[{row.ExpectedVisits}] actual=[{actualVisits}]; " +
                $"entries expected=[{row.ExpectedEntries}] actual=[{actualEntries}]; " +
                $"flags expected=[{row.ExpectedFlags}] actual=[{actualFlags}]";
            output.WriteLine(observation);
            if (!passed)
                mismatches.Add(observation);
        }

        Assert.True(mismatches.Count == 0, "Map.forEach semantic mismatches:\n" + string.Join("\n", mismatches));

        static string DescribeArray(TsArray array) =>
            string.Join(",", Enumerable.Range(0, array.Count).Select(i => Describe(array.Get(i))));

        static string Describe(TsValue value) => value switch
        {
            TsStringValue str => str.Value,
            TsInt32Value number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TsBoolValue flag => flag.Value ? "true" : "false",
            _ => $"<{value.GetType().Name}>",
        };
    }
}
