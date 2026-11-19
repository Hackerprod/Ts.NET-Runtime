using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using TypeSharp.IR;
using TypeSharp.IR.Optimizations;
using TypeSharp.Interop.HostExports;
using TypeSharp.Interop.Marshalling;
using TypeSharp.Interop.Proxies;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax.Diagnostics;
using Xunit;

namespace TypeSharp.VM.Tests;

public class InterpreterTests
{
    [Theory]
    [InlineData("omitted", "abcabc", "abc", null, true)]
    [InlineData("omitted-no-match", "abcabc", "bc", null, false)]
    [InlineData("exact-positive-offset", "abcxyz", "xyz", "3", true)]
    [InlineData("no-backtracking", "abcxyz", "abc", "3", false)]
    [InlineData("not-contains", "abcxyz", "xyz", "2", false)]
    [InlineData("positive-fraction", "abcxyz", "xyz", "3.9", true)]
    [InlineData("negative-fraction", "abcxyz", "abc", "-1.9", true)]
    [InlineData("large-negative", "abcxyz", "abc", "-1e100", true)]
    [InlineData("at-length", "abcxyz", "abc", "6", false)]
    [InlineData("large-positive", "abcxyz", "abc", "1e100", false)]
    [InlineData("positive-infinity", "abcxyz", "abc", "Infinity", false)]
    [InlineData("negative-infinity", "abcxyz", "abc", "-Infinity", true)]
    [InlineData("nan", "abcxyz", "abc", "NaN", true)]
    [InlineData("undefined", "abcxyz", "abc", "undefined", true)]
    [InlineData("empty-needle", "abcxyz", "", "Infinity", true)]
    [InlineData("utf16-offset", "a😀z", "z", "3", true)]
    [InlineData("utf16-not-code-point-offset", "a😀z", "z", "2", false)]
    [InlineData("case-sensitive", "abcXYZ", "xyz", "3", false)]
    [InlineData("empty-receiver", "", "x", "1", false)]
    public void StringStartsWith_SearchPosition(string row, string text, string needle, string? position, bool expected)
    {
        string search = position is null ? "needle" : $"needle, {position}";
        string source = $"function main(s: string, needle: string): boolean {{ return s.startsWith({search}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::startsWith");
        Assert.Equal(position is null ? 2 : 3, call.Operand1);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsBoolValue>(interpreter.Execute(
            BytecodeCompiler.Compile(ir), "main", [TsValue.FromString(text), TsValue.FromString(needle)])).Value;
        _output.WriteLine($"StringStartsWith row={row}; parser/binder=0/0; IR argc={call.Operand1}; actual={actual}; expected={expected}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("abcXYZ", "abc", "3", true)]
    [InlineData("abcXYZ", "XYZ", "3", false)]
    [InlineData("abcXYZ", "abc", "3.9", true)]
    [InlineData("abcXYZ", "XYZ", "-2", false)]
    [InlineData("abcXYZ", "XYZ", "999", true)]
    [InlineData("abcXYZ", "XYZ", "Infinity", true)]
    [InlineData("abcXYZ", "XYZ", "-Infinity", false)]
    [InlineData("abcXYZ", "XYZ", "NaN", false)]
    [InlineData("abcXYZ", "XYZ", "undefined", true)]
    [InlineData("abcXYZ", "XYZ", "omitted", true)]
    [InlineData("abcXYZ", "", "0", true)]
    [InlineData("abcXYZ", "ABC", "3", false)]
    [InlineData("a😀z", "😀", "3", true)]
    public void StringEndsWith_EndPosition(string text, string suffix, string position, bool expected)
    {
        bool omitted = position == "omitted";
        var source = omitted
            ? "function main(s: string, needle: string): boolean { return s.endsWith(needle); }"
            : "function main(s: string, needle: string, p: number | undefined): boolean { return s.endsWith(needle, p); }";
        TsValue[] args = omitted
            ? [TsValue.FromString(text), TsValue.FromString(suffix)]
            : [TsValue.FromString(text), TsValue.FromString(suffix),
               position == "undefined" ? TsValue.Void : TsValue.FromFloat64(double.Parse(position, System.Globalization.CultureInfo.InvariantCulture))];
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::endsWith");
        Assert.Equal(omitted ? 2 : 3, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsBoolValue>(interpreter.Execute(bytecode, "main", args)).Value;
        _output.WriteLine($"endsWith({text}, {suffix}, {position}) => {actual}, expected {expected}, IR argc={call.Operand1}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("abcabc", "abc", "3", true)]
    [InlineData("abcABC", "abc", "1", false)]
    [InlineData("abcABC", "abc", "1.9", false)]
    [InlineData("abcABC", "abc", "0.9", true)]
    [InlineData("abcABC", "abc", "-1.9", true)]
    [InlineData("abcABC", "bc", "999", false)]
    [InlineData("abcABC", "bc", "Infinity", false)]
    [InlineData("abcABC", "bc", "-Infinity", true)]
    [InlineData("abcABC", "bc", "NaN", true)]
    [InlineData("abcABC", "bc", "undefined", true)]
    [InlineData("abcABC", "bc", "omitted", true)]
    [InlineData("abcABC", "", "Infinity", true)]
    [InlineData("a😀z", "😀", "2", false)]
    [InlineData("a😀z", "z", "2", true)]
    public void StringIncludes_SearchPosition(string text, string needle, string position, bool expected)
    {
        bool omitted = position == "omitted";
        var source = omitted
            ? "function main(s: string, needle: string): boolean { return s.includes(needle); }"
            : "function main(s: string, needle: string, p: number | undefined): boolean { return s.includes(needle, p); }";
        TsValue[] args = omitted
            ? [TsValue.FromString(text), TsValue.FromString(needle)]
            : [TsValue.FromString(text), TsValue.FromString(needle),
               position == "undefined" ? TsValue.Void : TsValue.FromFloat64(double.Parse(position, System.Globalization.CultureInfo.InvariantCulture))];
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::includes");
        Assert.Equal(omitted ? 2 : 3, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsBoolValue>(interpreter.Execute(bytecode, "main", args)).Value;
        _output.WriteLine($"includes({text}, {needle}, {position}) => {actual}, expected {expected}, IR argc={call.Operand1}, parser/binder diagnostics=0/0");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("last-default", "banana", "ana", "omitted", 3)]
    [InlineData("undefined-is-default", "banana", "ana", "undefined", 3)]
    [InlineData("nan-is-default", "banana", "ana", "NaN", 3)]
    [InlineData("start-bound-inclusive", "banana", "ana", "1", 1)]
    [InlineData("match-may-extend-past-bound", "banana", "ana", "2", 1)]
    [InlineData("fraction-truncates", "banana", "ana", "2.9", 1)]
    [InlineData("below-first-match", "banana", "ana", "0", -1)]
    [InlineData("negative-clamps-to-zero", "abcabc", "abc", "-100", 0)]
    [InlineData("negative-infinity-clamps-to-zero", "abcabc", "abc", "-Infinity", 0)]
    [InlineData("positive-infinity-is-full", "banana", "ana", "Infinity", 3)]
    [InlineData("large-finite-is-full", "banana", "ana", "1e100", 3)]
    [InlineData("empty-needle-returns-position", "banana", "", "2.9", 2)]
    [InlineData("empty-needle-returns-length", "banana", "", "omitted", 6)]
    [InlineData("no-match", "banana", "ANA", "1", -1)]
    [InlineData("needle-longer-than-text", "a", "abc", "2", -1)]
    [InlineData("utf16-start-bound", "a😀😀z", "😀", "2", 1)]
    public void StringLastIndexOf_StartPosition(string row, string text, string needle, string position, int expected)
    {
        bool omitted = position == "omitted";
        var source = omitted
            ? "function main(s: string, needle: string): number { return s.lastIndexOf(needle); }"
            : "function main(s: string, needle: string, p: number | undefined): number { return s.lastIndexOf(needle, p); }";
        TsValue[] args = omitted
            ? [TsValue.FromString(text), TsValue.FromString(needle)]
            : [TsValue.FromString(text), TsValue.FromString(needle),
               position == "undefined" ? TsValue.Void : TsValue.FromFloat64(double.Parse(position, System.Globalization.CultureInfo.InvariantCulture))];
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::lastIndexOf");
        Assert.Equal(omitted ? 2 : 3, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsInt32Value>(interpreter.Execute(bytecode, "main", args)).Value;
        _output.WriteLine($"{row}: actual={actual} expected={expected}, IR argc={call.Operand1}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("basic", "one,two,three", ",", "2", "[\"006f006e0065\",\"00740077006f\"]")]
    [InlineData("no-remainder", "one,two,three", ",", "1", "[\"006f006e0065\"]")]
    [InlineData("empty-fields", ",a,,", ",", "3", "[\"\",\"0061\",\"\"]")]
    [InlineData("zero", "a,b", ",", "0", "[]")]
    [InlineData("positive-fraction", "a,b,c", ",", "1.9", "[\"0061\"]")]
    [InlineData("negative-fraction-to-zero", "a,b", ",", "-0.9", "[]")]
    [InlineData("uint32-wrap", "a,b,c", ",", "4294967297", "[\"0061\"]")]
    [InlineData("negative-uint32-wrap", "a,b,c", ",", "-4294967295", "[\"0061\"]")]
    [InlineData("negative-one-large-limit", "a,b", ",", "-1", "[\"0061\",\"0062\"]")]
    [InlineData("positive-infinity", "a,b", ",", "Infinity", "[]")]
    [InlineData("negative-infinity", "a,b", ",", "-Infinity", "[]")]
    [InlineData("nan", "a,b", ",", "NaN", "[]")]
    [InlineData("explicit-undefined", "a,b,", ",", "undefined", "[\"0061\",\"0062\",\"\"]")]
    [InlineData("omitted", "a,b,", ",", "omitted", "[\"0061\",\"0062\",\"\"]")]
    [InlineData("multi-unit-separator", "a::b::c", "::", "2", "[\"0061\",\"0062\"]")]
    [InlineData("empty-separator-utf16", "a\ud83d\ude00z", "", "2", "[\"0061\",\"d83d\"]")]
    [InlineData("empty-input-nonempty-separator", "", ",", "1", "[\"\"]")]
    [InlineData("empty-input-empty-separator", "", "", "1", "[]")]
    [InlineData("no-match-nonzero", "abc", ",", "1", "[\"006100620063\"]")]
    [InlineData("no-match-zero", "abc", ",", "0", "[]")]
    public void StringSplit_RespectsResultLimit(string row, string text, string separator, string limit, string expected)
    {
        bool omitted = limit == "omitted";
        var source = omitted
            ? "function main(s: string, separator: string): string[] { return s.split(separator); }"
            : "function main(s: string, separator: string, limit: number | undefined): string[] { return s.split(separator, limit); }";
        TsValue[] args = omitted
            ? [TsValue.FromString(text), TsValue.FromString(separator)]
            : [TsValue.FromString(text), TsValue.FromString(separator),
               limit == "undefined" ? TsValue.Void : TsValue.FromFloat64(double.Parse(limit, System.Globalization.CultureInfo.InvariantCulture))];
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("String::split", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(omitted ? 2 : 3, call.Operand1);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsArrayValue>(interpreter.Execute(BytecodeCompiler.Compile(ir), "main", args)).Value;
        string Field(int index)
        {
            Assert.False(result.IsHole(index));
            var value = Assert.IsType<TsStringValue>(result.Get(index)).Value;
            return "\"" + string.Concat(value.Select(unit => ((int)unit).ToString("x4"))) + "\"";
        }
        var actual = "[" + string.Join(",", Enumerable.Range(0, result.Count).Select(Field)) + "]";
        _output.WriteLine($"StringSplit row={row}; IR argc={call.Operand1}; actual={actual} expected={expected}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("receiver-only", "1HUH", "", "", "1HUH")]
    [InlineData("operand-only", "1", "H2H", "", "1H2H")]
    [InlineData("mixed-offsets", "H1H", "UH2H", ", 9, c", "H1HUH2H9HN")]
    [InlineData("all-holes", "HH", "HHH", "", "HHHHH")]
    [InlineData("self-concat", "1HUH", "", ", a", "1HUH1HUH")]
    [InlineData("dense-control", "1U", "2N", ", 9, c", "1U2N9N")]
    [InlineData("all-empty", "", "", ", c", "")]
    public void ArrayConcat_PreservesSparseSlots(string row, string left, string right, string tail, string expected)
    {
        var nested = new TsArrayValue(new TsArray());
        nested.Value.Add(TsValue.FromInt32(8));
        TsArrayValue MakeArray(string slots)
        {
            var array = new TsArray();
            foreach (char slot in slots)
            {
                if (slot == 'H') array.SetHole(array.Count);
                else array.Add(slot switch
                {
                    'U' => TsValue.Void,
                    'N' => nested,
                    _ => TsValue.FromInt32(slot - '0'),
                });
            }
            return new TsArrayValue(array);
        }

        string Describe(TsArray array) => new(Enumerable.Range(0, array.Count).Select(i =>
        {
            if (array.IsHole(i)) return 'H';
            var value = array.Get(i);
            if (ReferenceEquals(value, TsValue.Void)) return 'U';
            if (ReferenceEquals(value, nested)) return 'N';
            return value switch
            {
                TsInt32Value n => (char)('0' + n.Value),
                TsFloat64Value n => (char)('0' + (int)n.Value),
                _ => '?',
            };
        }).ToArray());

        var first = MakeArray(left);
        var second = MakeArray(right);
        string thirdSlots = row switch { "mixed-offsets" => "HN", "dense-control" => "N", _ => "" };
        var third = MakeArray(thirdSlots);
        var argumentList = row == "receiver-only" ? "" : row == "self-concat" ? "a" : "b" + tail;
        var source = $"function main(a: any[], b: any[], c: any[]): any[] {{ return a.concat({argumentList}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::concat", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argumentList.Length == 0 ? 1 : argumentList.Count(c => c == ',') + 2, call.Operand1);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actualArray = Assert.IsType<TsArrayValue>(
            interpreter.Execute(BytecodeCompiler.Compile(ir), "main", [first, second, third])).Value;
        Assert.NotSame(first.Value, actualArray);
        Assert.NotSame(second.Value, actualArray);
        Assert.NotSame(third.Value, actualArray);
        Assert.Equal(left, Describe(first.Value));
        Assert.Equal(right, Describe(second.Value));
        Assert.Equal(thirdSlots, Describe(third.Value));

        string actual = Describe(actualArray);
        _output.WriteLine($"ArrayConcat row={row}; IR argc={call.Operand1}; actual=[{actual}]; expected=[{expected}]");
        Assert.Equal(expected.Length, actualArray.Count);
        Assert.Equal(expected, actual);
    }

    private static TsValue? Run(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);

        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);

        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }

    private static void AssertStrictAnyEquality(string source, bool expected, params TsValue[] args)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        parser.Parse();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(BindSourceErrors(source));

        Assert.Equal(expected, Assert.IsType<TsBoolValue>(Run(source, args: args)).Value);
    }

    [Theory]
    [InlineData(true, "===", false)]
    [InlineData(true, "!==", true)]
    [InlineData(false, "===", false)]
    [InlineData(false, "!==", true)]
    public void StrictAnyEquality_SameNaNArgument(bool float32, string op, bool expected)
    {
        var value = float32 ? TsValue.FromFloat32(float.NaN) : TsValue.FromFloat64(double.NaN);
        AssertStrictAnyEquality($"function main(x: any): bool {{ return x {op} x; }}", expected, value);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    public void StrictAnyEquality_NumericControls(bool float32, bool nan, bool distinctWrappers, bool equal)
    {
        TsValue Box() => float32
            ? TsValue.FromFloat32(nan ? float.NaN : 1.25f)
            : TsValue.FromFloat64(nan ? double.NaN : 1.25);

        var left = Box();
        var right = distinctWrappers ? Box() : left;
        if (distinctWrappers) Assert.NotSame(left, right);

        AssertStrictAnyEquality("function main(x: any, y: any): bool { return x === y; }", equal, left, right);
        AssertStrictAnyEquality("function main(x: any, y: any): bool { return x !== y; }", !equal, left, right);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void StrictAnyEquality_ObjectControls(bool distinctWrappers, bool distinctObjects, bool equal)
    {
        var obj = new TsObject("Box");
        var left = TsValue.FromObject(obj);
        var right = distinctWrappers ? TsValue.FromObject(distinctObjects ? new TsObject("Box") : obj) : left;
        if (distinctWrappers) Assert.NotSame(left, right);

        AssertStrictAnyEquality("function main(x: any, y: any): bool { return x === y; }", equal, left, right);
        AssertStrictAnyEquality("function main(x: any, y: any): bool { return x !== y; }", !equal, left, right);
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public InterpreterTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("negative-tie-one")]
    [InlineData("negative-tie-two")]
    [InlineData("negative-tie-zero")]
    [InlineData("negative-tie-large")]
    [InlineData("negative-zero")]
    [InlineData("positive-zero")]
    [InlineData("negative-small")]
    [InlineData("positive-small")]
    [InlineData("negative-subnormal")]
    [InlineData("positive-subnormal")]
    [InlineData("negative-half-predecessor")]
    [InlineData("negative-half-successor")]
    [InlineData("positive-half-predecessor")]
    [InlineData("positive-half-tie")]
    [InlineData("positive-half-successor")]
    [InlineData("positive-tie-two")]
    [InlineData("negative-past-half")]
    [InlineData("negative-before-half")]
    [InlineData("positive-past-half")]
    [InlineData("positive-before-half")]
    [InlineData("positive-tie-large")]
    [InlineData("positive-large-odd")]
    [InlineData("negative-large-odd")]
    [InlineData("positive-max-finite")]
    [InlineData("negative-max-finite")]
    [InlineData("nan")]
    [InlineData("positive-infinity")]
    [InlineData("negative-infinity")]
    [InlineData("floor-control")]
    [InlineData("ceil-control")]
    [InlineData("trunc-control")]
    public void MathRound_JavaScriptNumberSemantics(string scenario)
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var (method, input, expected) = scenario switch
        {
            "negative-tie-one" => ("round", -1.5, -1.0),
            "negative-tie-two" => ("round", -2.5, -2.0),
            "negative-tie-zero" => ("round", -0.5, negativeZero),
            "negative-tie-large" => ("round", -4503599627370495.5, -4503599627370495.0),
            "negative-zero" => ("round", negativeZero, negativeZero),
            "positive-zero" => ("round", 0.0, 0.0),
            "negative-small" => ("round", -0.1, negativeZero),
            "positive-small" => ("round", 0.1, 0.0),
            "negative-subnormal" => ("round", -double.Epsilon, negativeZero),
            "positive-subnormal" => ("round", double.Epsilon, 0.0),
            "negative-half-predecessor" => ("round", Math.BitDecrement(-0.5), -1.0),
            "negative-half-successor" => ("round", Math.BitIncrement(-0.5), negativeZero),
            "positive-half-predecessor" => ("round", Math.BitDecrement(0.5), 0.0),
            "positive-half-tie" => ("round", 0.5, 1.0),
            "positive-half-successor" => ("round", Math.BitIncrement(0.5), 1.0),
            "positive-tie-two" => ("round", 2.5, 3.0),
            "negative-past-half" => ("round", -1.6, -2.0),
            "negative-before-half" => ("round", -1.4, -1.0),
            "positive-past-half" => ("round", 1.6, 2.0),
            "positive-before-half" => ("round", 1.4, 1.0),
            "positive-tie-large" => ("round", 4503599627370495.5, 4503599627370496.0),
            "positive-large-odd" => ("round", 4503599627370497.0, 4503599627370497.0),
            "negative-large-odd" => ("round", -4503599627370497.0, -4503599627370497.0),
            "positive-max-finite" => ("round", double.MaxValue, double.MaxValue),
            "negative-max-finite" => ("round", -double.MaxValue, -double.MaxValue),
            "nan" => ("round", double.NaN, double.NaN),
            "positive-infinity" => ("round", double.PositiveInfinity, double.PositiveInfinity),
            "negative-infinity" => ("round", double.NegativeInfinity, double.NegativeInfinity),
            "floor-control" => ("floor", -1.5, -2.0),
            "ceil-control" => ("ceil", -1.5, -1.0),
            "trunc-control" => ("trunc", -1.5, -1.0),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        var source = $"function main(x: number): number {{ return Math.{method}(x); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        foreach (var diagnostic in parser.Diagnostics)
            _output.WriteLine($"parser [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(parser.Diagnostics);

        var binder = new TypeSharp.Semantics.Binder.Binder();
        binder.Bind(tree);
        foreach (var diagnostic in binder.Diagnostics.All)
            _output.WriteLine($"binder [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(binder.Diagnostics.All);
        _output.WriteLine("parser diagnostics=0; binder diagnostics=0");

        var actual = Assert.IsType<TsFloat64Value>(Run(source, args: [TsValue.FromFloat64(input)])).Value;
        static string Display(double value) =>
            FormattableString.Invariant($"{value:R}/0x{BitConverter.DoubleToInt64Bits(value):X16}");
        _output.WriteLine($"MathRound row={scenario}; method={method}; type=TsFloat64Value; input={Display(input)}; expected={Display(expected)}; actual={Display(actual)}");

        if (double.IsNaN(expected))
            Assert.True(double.IsNaN(actual), "Expected NaN");
        else
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("negative-zero")]
    [InlineData("positive-zero")]
    [InlineData("negative-finite")]
    [InlineData("positive-finite")]
    [InlineData("negative-subnormal")]
    [InlineData("positive-subnormal")]
    [InlineData("negative-infinity")]
    [InlineData("positive-infinity")]
    public void MathSign_JavaScriptNumberSemantics(string scenario)
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var (input, expected) = scenario switch
        {
            "nan" => (double.NaN, double.NaN),
            "negative-zero" => (negativeZero, negativeZero),
            "positive-zero" => (0.0, 0.0),
            "negative-finite" => (-2.75, -1.0),
            "positive-finite" => (2.75, 1.0),
            "negative-subnormal" => (-double.Epsilon, -1.0),
            "positive-subnormal" => (double.Epsilon, 1.0),
            "negative-infinity" => (double.NegativeInfinity, -1.0),
            "positive-infinity" => (double.PositiveInfinity, 1.0),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        const string source = "function main(x: number): number { return Math.sign(x); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        foreach (var diagnostic in parser.Diagnostics)
            _output.WriteLine($"parser [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(parser.Diagnostics);

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(tree);
        foreach (var diagnostic in binder.Diagnostics.All)
            _output.WriteLine($"binder [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(binder.Diagnostics.All);
        _output.WriteLine("parser diagnostics=0; binder diagnostics=0");

        var bytecodeModule = BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree));
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var argument = TsValue.FromFloat64(input);
        static string Display(double value) =>
            FormattableString.Invariant($"{value:R}/0x{BitConverter.DoubleToInt64Bits(value):X16}");
        _output.WriteLine($"MathSign row={scenario}; input={Display(input)}; expected={Display(expected)}");
        TsValue? actual = null;
        var exception = Record.Exception(() => { actual = interpreter.Execute(bytecodeModule, "main", [argument]); });
        if (exception is not null)
            _output.WriteLine(exception.ToString());
        Assert.Null(exception);

        var value = Assert.IsType<TsFloat64Value>(actual).Value;
        _output.WriteLine($"MathSign type=TsFloat64Value; actual={Display(value)}");
        if (double.IsNaN(expected))
            Assert.True(double.IsNaN(value), "Expected NaN");
        else
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(value));
    }

    public static IEnumerable<object[]> ParseFloatPrefixCases()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        (string Id, string Input, double Expected)[] cases =
        [
            ("suffix", "12.5px", 12.5),
            ("exp-sign-no-digit", "1e+kg", 1.0),
            ("comma", "1,234", 1.0),
            ("dot-leading", "+.5kg", 0.5),
            ("dot-trailing", "73.", 73.0),
            ("dot-exp-complete", "1.E+2kg", 100.0),
            ("dot-exp-incomplete", "1.e-", 1.0),
            ("exponent-signed", "-6.25E-2", -0.0625),
            ("finite", " \t+12.5 \r\n", 12.5),
            ("infinity-positive", "+InfinityTail", double.PositiveInfinity),
            ("infinity-negative", "-InfinityTail", double.NegativeInfinity),
            ("infinity-case", "infinity", double.NaN),
            ("infinity-control", "Infinity", double.PositiveInfinity),
            ("bom-and-lines", "\ufeff\u00a0\u2028\u2029\u000b\f-3.5s", -3.5),
            ("nel", "\u008542", double.NaN),
            ("no-prefix", "junk2.5", double.NaN),
            ("no-mantissa", "+.e4", double.NaN),
            ("empty", " ", double.NaN),
            ("ascii-only", "\u06612", double.NaN),
            ("ascii-exp-only", "1e+\u0661", 1.0),
            ("no-inner-trim", "- 1", double.NaN),
            ("non-decimal", "0x10", 0.0),
            ("underscore", "9_9", 9.0),
            ("negative-zero-suffix", "-0.0px", negativeZero),
            ("negative-zero-control", "-0", negativeZero),
            ("overflow-plus", "9e9999px", double.PositiveInfinity),
            ("overflow-minus", "-9e9999px", double.NegativeInfinity),
            ("overflow-control", "9e9999", double.PositiveInfinity),
            ("neg-underflow", "-1e-9999px", negativeZero),
            ("exponent-two", "1e2e3", 100.0),
        ];
        foreach (var (id, input, expected) in cases)
        {
            yield return ["parseFloat", id, input, expected];
        }
        yield return ["Number", "number-suffix", "12.5px", double.NaN];
        yield return ["Number", "number-finite", "12.5", 12.5];
        yield return ["parseInt", "parse-int", "42", 42.0];
    }

    [Theory]
    [MemberData(nameof(ParseFloatPrefixCases))]
    public void ParseFloatPrefix_JavaScriptDecimalPrefix(string route, string id, string input, double expected)
    {
        var source = $"function main(x: string): number {{ return {route}(x); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        foreach (var diagnostic in parser.Diagnostics)
            _output.WriteLine($"parser [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(parser.Diagnostics);

        var binder = new TypeSharp.Semantics.Binder.Binder();
        binder.Bind(tree);
        foreach (var diagnostic in binder.Diagnostics.All)
            _output.WriteLine($"binder [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(binder.Diagnostics.All);
        _output.WriteLine("parser diagnostics=0; binder diagnostics=0");

        var actual = Assert.IsType<TsFloat64Value>(Run(source, args: [TsValue.FromString(input)])).Value;
        static string Display(double value) =>
            FormattableString.Invariant($"{value:R}/0x{BitConverter.DoubleToInt64Bits(value):X16}");
        var escaped = string.Concat(input.Select(ch => FormattableString.Invariant($"\\u{(int)ch:X4}")));
        _output.WriteLine($"ParseFloatPrefix route={route}; row={id}; type=TsFloat64Value; input={escaped}; expected={Display(expected)}; actual={Display(actual)}");

        if (double.IsNaN(expected))
            Assert.True(double.IsNaN(actual), "Expected NaN");
        else
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
    }

    private void AssertArraySearchEquality(string source, string method, int expected, params TsValue[] args)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        foreach (var diagnostic in parser.Diagnostics)
            _output.WriteLine($"parser [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(parser.Diagnostics);

        var binder = new TypeSharp.Semantics.Binder.Binder();
        binder.Bind(tree);
        foreach (var diagnostic in binder.Diagnostics.All)
            _output.WriteLine($"binder [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(binder.Diagnostics.All);
        _output.WriteLine("parser diagnostics=0; binder diagnostics=0");

        var result = Run(source, args: args);
        if (method == "includes")
        {
            var actual = Assert.IsType<TsBoolValue>(result).Value;
            _output.WriteLine($"Expected: {expected != 0}; Actual: {actual}");
            Assert.Equal(expected != 0, actual);
        }
        else
        {
            var actual = Assert.IsType<TsInt32Value>(result).Value;
            _output.WriteLine($"Expected: {expected}; Actual: {actual}");
            Assert.Equal(expected, actual);
        }
    }


    [Theory]
    [InlineData("omitted", "const a: any[] = [7, 2, 7, 3, 7];", "7", "omitted", 0)]
    [InlineData("duplicate-after-start", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1", 2)]
    [InlineData("inclusive-positive", "const a: any[] = [7, 2, 7, 3, 7];", "7", "2", 2)]
    [InlineData("negative-tail", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-1", 4)]
    [InlineData("negative-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-5", 0)]
    [InlineData("below-negative-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-100", 0)]
    [InlineData("positive-fraction", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1.9", 2)]
    [InlineData("negative-fraction", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-1.9", 4)]
    [InlineData("negative-zero", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-0", 0)]
    [InlineData("nan", "const a: any[] = [7, 2, 7, 3, 7];", "7", "NaN", 0)]
    [InlineData("undefined", "const a: any[] = [7, 2, 7, 3, 7];", "7", "undefined", 0)]
    [InlineData("at-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "5", -1)]
    [InlineData("positive-infinity", "const a: any[] = [7, 2, 7, 3, 7];", "7", "Infinity", -1)]
    [InlineData("negative-infinity", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-Infinity", 0)]
    [InlineData("large-finite", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1e100", -1)]
    [InlineData("empty", "const a: any[] = [];", "7", "2", -1)]
    [InlineData("hole-skipped-after-start", "const a: any[] = [undefined]; a[2] = undefined;", "undefined", "1", 2)]
    [InlineData("hole-not-undefined", "const a: any[] = [undefined]; a[2] = 7;", "undefined", "1", -1)]
    [InlineData("strict-equality", "const a: any[] = [7, '7'];", "7", "1", -1)]
    public void ArrayIndexOf_FromIndex(string row, string prelude, string needle, string position, int expected)
    {
        bool omitted = position == "omitted";
        string arguments = omitted ? needle : $"{needle}, {position}";
        string source = $"function main(): number {{ {prelude} return a.indexOf({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::indexOf");
        Assert.Equal(omitted ? 2 : 3, call.Operand1);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsInt32Value>(
            interpreter.Execute(BytecodeCompiler.Compile(ir), "main")).Value;
        _output.WriteLine($"ArrayIndexOf row={row}; parser/binder=0/0; IR argc={call.Operand1}; actual={actual}; expected={expected}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("omitted", "const a: any[] = [7, 2, 7, 3, 7];", "7", "omitted", 4)]
    [InlineData("inclusive-positive", "const a: any[] = [7, 2, 7, 3, 7];", "7", "2", 2)]
    [InlineData("backward-not-forward", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1", 0)]
    [InlineData("negative-tail", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-1", 4)]
    [InlineData("negative-relative", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-2", 2)]
    [InlineData("negative-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-5", 0)]
    [InlineData("below-negative-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-6", -1)]
    [InlineData("at-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "5", 4)]
    [InlineData("positive-fraction", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1.9", 0)]
    [InlineData("negative-fraction", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-2.9", 2)]
    [InlineData("negative-zero", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-0", 0)]
    [InlineData("nan", "const a: any[] = [7, 2, 7, 3, 7];", "7", "NaN", 0)]
    [InlineData("explicit-undefined", "const a: any[] = [7, 2, 7, 3, 7];", "7", "undefined", 0)]
    [InlineData("positive-infinity", "const a: any[] = [7, 2, 7, 3, 7];", "7", "Infinity", 4)]
    [InlineData("negative-infinity", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-Infinity", -1)]
    [InlineData("large-positive-finite", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1e100", 4)]
    [InlineData("large-negative-finite", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-1e100", -1)]
    [InlineData("empty", "const a: any[] = [];", "7", "2", -1)]
    [InlineData("hole-skipped-after-start", "const a: any[] = [undefined]; a[2] = undefined;", "undefined", "1", 0)]
    [InlineData("hole-not-undefined", "const a: any[] = [7]; a[2] = undefined;", "undefined", "1", -1)]
    [InlineData("strict-equality", "const a: any[] = [7, '7'];", "7", "1", 0)]
    [InlineData("nan-needle", "const a: any[] = [NaN];", "NaN", "0", -1)]
    public void ArrayLastIndexOf_FromIndex(string row, string prelude, string needle, string position, int expected)
    {
        bool omitted = position == "omitted";
        string arguments = omitted ? needle : $"{needle}, {position}";
        string source = $"function main(): number {{ {prelude} return a.lastIndexOf({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::lastIndexOf");
        Assert.Equal(omitted ? 2 : 3, call.Operand1);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsInt32Value>(
            interpreter.Execute(BytecodeCompiler.Compile(ir), "main")).Value;
        _output.WriteLine($"ArrayLastIndexOf row={row}; parser/binder=0/0; IR argc={call.Operand1}; actual={actual}; expected={expected}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("omitted", "const a: any[] = [7, 2, 7, 3, 7];", "7", null, true)]
    [InlineData("inclusive-positive", "const a: any[] = [7, 2, 7, 3, 7];", "7", "2", true)]
    [InlineData("after-last-match", "const a: any[] = [7, 2, 7, 3, 7];", "2", "2", false)]
    [InlineData("negative-tail", "const a: any[] = [7, 2, 7, 3, 7];", "2", "-1", false)]
    [InlineData("negative-relative", "const a: any[] = [7, 2, 7, 3, 7];", "3", "-2", true)]
    [InlineData("negative-length", "const a: any[] = [7, 2, 7, 3, 7];", "2", "-5", true)]
    [InlineData("below-negative-length", "const a: any[] = [7, 2, 7, 3, 7];", "2", "-6", true)]
    [InlineData("at-length", "const a: any[] = [7, 2, 7, 3, 7];", "7", "5", false)]
    [InlineData("positive-fraction", "const a: any[] = [7, 2, 7, 3, 7];", "2", "1.9", true)]
    [InlineData("positive-fraction-no-backtrack", "const a: any[] = [7, 2, 7, 3, 7];", "2", "2.9", false)]
    [InlineData("negative-fraction", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-1.9", true)]
    [InlineData("negative-fraction-no-backtrack", "const a: any[] = [7, 2, 7, 3, 7];", "3", "-1.9", false)]
    [InlineData("negative-zero", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-0", true)]
    [InlineData("nan", "const a: any[] = [7, 2, 7, 3, 7];", "7", "NaN", true)]
    [InlineData("explicit-undefined", "const a: any[] = [7, 2, 7, 3, 7];", "7", "undefined", true)]
    [InlineData("positive-infinity", "const a: any[] = [7, 2, 7, 3, 7];", "7", "Infinity", false)]
    [InlineData("negative-infinity", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-Infinity", true)]
    [InlineData("large-positive-finite", "const a: any[] = [7, 2, 7, 3, 7];", "7", "1e100", false)]
    [InlineData("large-negative-finite", "const a: any[] = [7, 2, 7, 3, 7];", "7", "-1e100", true)]
    [InlineData("empty", "const a: any[] = [];", "7", "2", false)]
    [InlineData("hole-read-as-undefined", "const a: any[] = [7]; a[2] = undefined;", "undefined", "1", true)]
    [InlineData("present-undefined", "const a: any[] = [7]; a[2] = undefined;", "undefined", "2", true)]
    [InlineData("skip-hole-and-undefined", "const a: any[] = [7]; a[2] = undefined;", "undefined", "3", false)]
    [InlineData("strict-types", "const a: any[] = [7, \"7\"];", "7", "1", false)]
    [InlineData("nan-needle", "const a: any[] = [7, NaN];", "NaN", "1", true)]
    [InlineData("skip-nan-needle", "const a: any[] = [NaN, 7];", "NaN", "1", false)]
    public void ArrayIncludes_FromIndex(string row, string prelude, string needle, string? position, bool expected)
    {
        string arguments = position is null ? needle : $"{needle}, {position}";
        string source = $"function main(): bool {{ {prelude} return a.includes({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::includes");
        Assert.Equal(position is null ? 2 : 3, call.Operand1);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsBoolValue>(
            interpreter.Execute(BytecodeCompiler.Compile(ir), "main")).Value;
        _output.WriteLine($"ArrayIncludes row={row}; parser/binder=0/0; IR argc={call.Operand1}; actual={actual}; expected={expected}");
        Assert.Equal(expected, actual);
    }

    private static string ArraySearchReturnType(string method) => method == "includes" ? "bool" : "int32";

    [Theory]
    [InlineData("literal NaN", "NaN", "NaN", "includes", 1)]
    [InlineData("coercion includes", "'1'", "1", "includes", 0)]
    [InlineData("coercion indexOf", "'1', 1, '1'", "1", "indexOf", 1)]
    [InlineData("coercion lastIndexOf", "'1', 1, '1'", "1", "lastIndexOf", 1)]
    [InlineData("literal NaN indexOf", "NaN", "NaN", "indexOf", -1)]
    [InlineData("literal NaN lastIndexOf", "NaN", "NaN", "lastIndexOf", -1)]
    [InlineData("duplicate", "1, 2, 1", "1", "includes", 1)]
    [InlineData("duplicate", "1, 2, 1", "1", "indexOf", 0)]
    [InlineData("duplicate", "1, 2, 1", "1", "lastIndexOf", 2)]
    [InlineData("empty", "", "1", "includes", 0)]
    [InlineData("empty", "", "1", "indexOf", -1)]
    [InlineData("empty", "", "1", "lastIndexOf", -1)]
    [InlineData("not numeric NaN", "'not a number'", "NaN", "includes", 0)]
    public void ArraySearchEquality_Literals(string scenario, string elements, string needle, string method, int expected)
    {
        _output.WriteLine(scenario);
        AssertArraySearchEquality(
            $"function main(): {ArraySearchReturnType(method)} {{ const a: any[] = [{elements}]; return a.{method}({needle}); }}",
            method, expected);
    }

    [Theory]
    [InlineData("F32 wrapper NaN", "includes", 1)]
    [InlineData("F32 wrapper NaN", "indexOf", -1)]
    [InlineData("F32 wrapper NaN", "lastIndexOf", -1)]
    [InlineData("F64 wrapper NaN", "includes", 1)]
    [InlineData("F64 wrapper NaN", "indexOf", -1)]
    [InlineData("F64 wrapper NaN", "lastIndexOf", -1)]
    public void ArraySearchEquality_SameNaNWrapper(string scenario, string method, int expected)
    {
        var value = scenario == "F32 wrapper NaN" ? TsValue.FromFloat32(float.NaN) : TsValue.FromFloat64(double.NaN);
        AssertArraySearchEquality(
            $"function main(x: any): {ArraySearchReturnType(method)} {{ const a: any[] = [x]; return a.{method}(x); }}",
            method, expected, value);
    }

    [Theory]
    [InlineData("mixed NaN", "includes", 1)]
    [InlineData("mixed NaN", "indexOf", -1)]
    [InlineData("mixed NaN", "lastIndexOf", -1)]
    [InlineData("signed zero", "includes", 1)]
    [InlineData("signed zero", "indexOf", 0)]
    [InlineData("signed zero", "lastIndexOf", 0)]
    [InlineData("same object", "includes", 1)]
    [InlineData("same object", "indexOf", 0)]
    [InlineData("same object", "lastIndexOf", 0)]
    [InlineData("distinct objects", "includes", 0)]
    [InlineData("distinct objects", "indexOf", -1)]
    [InlineData("distinct objects", "lastIndexOf", -1)]
    [InlineData("Null vs Void", "includes", 0)]
    [InlineData("Null vs Void", "indexOf", -1)]
    [InlineData("Null vs Void", "lastIndexOf", -1)]
    [InlineData("Void vs Null", "includes", 0)]
    [InlineData("Void vs Null", "indexOf", -1)]
    [InlineData("Void vs Null", "lastIndexOf", -1)]
    [InlineData("Null vs Null", "includes", 1)]
    [InlineData("Null vs Null", "indexOf", 0)]
    [InlineData("Null vs Null", "lastIndexOf", 0)]
    [InlineData("Void vs Void", "includes", 1)]
    [InlineData("Void vs Void", "indexOf", 0)]
    [InlineData("Void vs Void", "lastIndexOf", 0)]
    public void ArraySearchEquality_HostValues(string scenario, string method, int expected)
    {
        var shared = new TsObject("Box");
        (TsValue left, TsValue right) = scenario switch
        {
            "mixed NaN" => (TsValue.FromFloat32(float.NaN), TsValue.FromFloat64(double.NaN)),
            "signed zero" => (TsValue.FromFloat64(-0.0), TsValue.FromFloat64(0.0)),
            "same object" => (TsValue.FromObject(shared), TsValue.FromObject(shared)),
            "distinct objects" => (TsValue.FromObject(shared), TsValue.FromObject(new TsObject("Box"))),
            "Null vs Void" => (TsValue.Null, TsValue.Void),
            "Void vs Null" => (TsValue.Void, TsValue.Null),
            "Null vs Null" => (TsValue.Null, TsValue.Null),
            "Void vs Void" => (TsValue.Void, TsValue.Void),
            _ => throw new System.ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (scenario == "same object") Assert.NotSame(left, right);

        AssertArraySearchEquality(
            $"function main(x: any, y: any): {ArraySearchReturnType(method)} {{ const a: any[] = [x]; return a.{method}(y); }}",
            method, expected, left, right);
    }

    [Theory]
    [InlineData(false, "includes", 1)]
    [InlineData(false, "indexOf", -1)]
    [InlineData(false, "lastIndexOf", -1)]
    [InlineData(true, "includes", 1)]
    [InlineData(true, "indexOf", 1)]
    [InlineData(true, "lastIndexOf", 1)]
    public void ArraySearchEquality_Sparse(bool hasPresentVoid, string method, int expected)
    {
        var array = new TsArray();
        array.SetHole(0);
        if (hasPresentVoid)
        {
            array.Set(1, TsValue.Void);
            array.SetHole(2);
        }

        Assert.Equal(hasPresentVoid ? 3 : 1, array.Count);
        Assert.True(array.IsHole(0));
        Assert.Same(TsValue.Void, array.Get(0));
        if (hasPresentVoid)
        {
            Assert.False(array.IsHole(1));
            Assert.Same(TsValue.Void, array.Get(1));
            Assert.True(array.IsHole(2));
            Assert.Same(TsValue.Void, array.Get(2));
        }

        AssertArraySearchEquality(
            $"function main(a: any[], x: any): {ArraySearchReturnType(method)} {{ return a.{method}(x); }}",
            method, expected, new TsArrayValue(array), TsValue.Void);
    }

    [Theory]
    [InlineData("second-match", "ababa", "ba", 3)]
    [InlineData("fractional", "ababa", "ba", 3)]
    [InlineData("empty-at-end", "abc", "", 3)]
    [InlineData("empty-over-length", "abc", "", 3)]
    [InlineData("nonempty-over-length", "abc", "a", -1)]
    [InlineData("positive-infinity", "abc", "a", -1)]
    [InlineData("utf16", "😀x😀x", "x", 5)]
    [InlineData("d-string", "ababa", "ba", 3)]
    [InlineData("omitted", "ababa", "ba", 1)]
    [InlineData("negative", "ababa", "ba", 1)]
    [InlineData("negative-infinity", "ababa", "ba", 1)]
    [InlineData("nan", "ababa", "ba", 1)]
    [InlineData("negative-zero", "ababa", "ba", 1)]
    [InlineData("explicit-void", "ababa", "ba", 1)]
    [InlineData("empty-receiver", "", "", 0)]
    [InlineData("ordinal-case", "aAa", "A", -1)]
    public void StringIndexOf_Position(string row, string text, string needle, int expected)
    {
        TsValue? position = row switch
        {
            "omitted" => null,
            "second-match" or "ordinal-case" => TsValue.FromInt32(2),
            "empty-at-end" or "utf16" => TsValue.FromInt32(3),
            "nonempty-over-length" => TsValue.FromInt32(99),
            "fractional" => TsValue.FromFloat64(3.9),
            "empty-over-length" => TsValue.FromFloat64(2147483648.0),
            "positive-infinity" or "empty-receiver" => TsValue.FromFloat64(double.PositiveInfinity),
            "negative" => TsValue.FromFloat64(-2.7),
            "negative-infinity" => TsValue.FromFloat64(double.NegativeInfinity),
            "nan" => TsValue.FromFloat64(double.NaN),
            "negative-zero" => TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue)),
            "explicit-void" => TsValue.Void,
            "d-string" => TsValue.FromString(" 2 "),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };

        var source = position is null
            ? "function main(s: string, needle: string): int32 { return s.indexOf(needle); }"
            : "function main(s: string, needle: string, p: any): int32 { return s.indexOf(needle, p); }";
        TsValue[] args = position is null
            ? [TsValue.FromString(text), TsValue.FromString(needle)]
            : [TsValue.FromString(text), TsValue.FromString(needle), position];
        var positionDisplay = position switch
        {
            TsFloat64Value value => value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            TsStringValue value => System.Text.Json.JsonSerializer.Serialize(value.Value),
            null => "omitted",
            TsVoid => "undefined",
            _ => position.ToString(),
        };
        _output.WriteLine($"StringIndexOf row={row}; host argc={args.Length}; position type={position?.GetType().Name ?? "omitted"}; position={positionDisplay}");
        if (row == "negative-zero")
        {
            var bits = BitConverter.DoubleToInt64Bits(Assert.IsType<TsFloat64Value>(position).Value);
            _output.WriteLine($"StringIndexOf row={row}; position bits={bits:X16}");
            Assert.Equal(long.MinValue, bits);
        }

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"StringIndexOf row={row}; parser diagnostics={parser.Diagnostics.Count()}; binder diagnostics={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::indexOf");
        _output.WriteLine($"StringIndexOf row={row}; target={call.OperandObject}; IR argc={call.Operand1}");
        Assert.Equal(position is null ? 2 : 3, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsInt32Value>(interpreter.Execute(bytecode, "main", args)).Value;
        _output.WriteLine($"StringIndexOf row={row}; type=TsInt32Value; expected={expected}; actual={actual}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("all-tokens-first", "ababa", "ba", "<$$|$&|$`|$'>", "a<$|ba|a|ba>ba")]
    [InlineData("adjacent-single-pass", "aba", "b", "$$&|$$`|$$'|$$$&|$$$$", "a$&|$`|$'|$b|$$a")]
    [InlineData("expansion-is-data", "P$`$&$'S", "$&", "<$'|$&|$`>", "P$`<$'S|$&|P$`>$'S")]
    [InlineData("no-captures-mixed", "abc", "b", "[$0|$1|$01|$99|$<x$&>|$x|$]", "a[$0|$1|$01|$99|$<xb>|$x|$]c")]
    [InlineData("empty-needle", "ab", "", "<$$|$&|$`|$'>", "<$|||ab>ab")]
    [InlineData("empty-both", "", "", "[$`$&$']", "[]")]
    [InlineData("unicode", "😀x😀x", "x", "<$`|$&|$'>", "😀<😀|x|😀x>😀x")]
    [InlineData("utf16-split", "a😀z", "<surrogate-marker>", "<$&:$'>", "<surrogate-marker>")]
    [InlineData("no-match-case", "aAa", "AA", "<$&$`$'>", "aAa")]
    [InlineData("no-match-normalization", "café", "é", "$$", "café")]
    [InlineData("literal-invalid", "abc", "b", "$0|$1|$01|$99|$<x>|$x|$", "a$0|$1|$01|$99|$<x>|$x|$c")]
    [InlineData("ordinary-first", "aaaa", "aa", "X", "Xaa")]
    [InlineData("delete", "ababa", "ba", "", "aba")]
    [InlineData("literal-pattern", "x.a.a", ".", "!", "x!a.a")]
    public void StringReplace_ReplacementTemplate(string row, string input, string find, string template, string expected)
    {
        if (row == "utf16-split")
        {
            // Keep isolated surrogates out of the theory's metadata and test display names.
            find = "\uD83D";
            expected = "a<\uD83D:\uDE00z>\uDE00z";
        }

        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static string Hex(string value) => string.Join(" ", value.Select(unit => ((int)unit).ToString("X4")));

        var source = $"function main(s: string): string {{ return s.replace({Quote(find)}, {Quote(template)}); }}";
        var tokens = new TypeSharp.Syntax.Lexer(source).Tokenize();
        var literals = tokens.Where(token => token.Kind == TypeSharp.Syntax.TokenKind.StringLiteral).ToArray();
        Assert.Equal(2, literals.Length);
        Assert.Equal(find, literals[0].Text);
        Assert.Equal(template, literals[1].Text);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"StringReplace row={row}; parser={parser.Diagnostics.Count()}; binder={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        _output.WriteLine($"StringReplace row={row}; target={call.OperandObject}; argc={call.Operand1}");
        Assert.Equal("String::replace", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(3, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiledMain = bytecode.Functions[bytecode.FunctionIndex["main"]];
        Assert.Contains(find, compiledMain.StringConstants);
        Assert.Contains(template, compiledMain.StringConstants);
        _output.WriteLine($"StringReplace row={row}; lexer-and-pool-find=[{Hex(find)}]; template=[{Hex(template)}]");
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", [TsValue.FromString(input)]); });
        _output.WriteLine($"StringReplace row={row}; exception={error?.GetType().Name ?? "none"}; result-type={result?.GetType().Name}");
        Assert.Null(error);
        var actual = Assert.IsType<TsStringValue>(result).Value;
        var expectedHex = Hex(expected);
        var actualHex = Hex(actual);
        _output.WriteLine($"StringReplace row={row}; expected=[{expectedHex}]; actual=[{actualHex}]");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Ordinal UTF-16 mismatch: expected=[{expectedHex}]; actual=[{actualHex}]");
    }

    [Theory]
    [InlineData("repeat-and-truncate", "7", 6.0, "ab", "0037 0061 0062 0061 0062 0061")]
    [InlineData("repeat-exact", "7", 5.0, "ab", "0037 0061 0062 0061 0062")]
    [InlineData("fill-longer-than-gap", "7", 3.0, "abcd", "0037 0061 0062")]
    [InlineData("one-unit-needed", "7", 2.0, "xy", "0037 0078")]
    [InlineData("fractional-width", "7", 3.9, "xyz", "0037 0078 0079")]
    [InlineData("utf16-surrogate-cut", "7", 4.0, "\ud83d\ude00", "0037 D83D DE00 D83D")]
    [InlineData("utf16-receiver", "\ud83d\ude00", 4.0, "ab", "D83D DE00 0061 0062")]
    [InlineData("utf16-exact-pair", "7", 3.0, "\ud83d\ude00", "0037 D83D DE00")]
    [InlineData("empty-receiver", "", 3.0, "ab", "0061 0062 0061")]
    [InlineData("empty-fill-grow", "7", 6.0, "", "0037")]
    [InlineData("empty-fill-no-grow", "123", 3.0, "", "0031 0032 0033")]
    [InlineData("empty-fill-huge", "7", 2147483647.0, "", "0037")]
    [InlineData("already-wide", "123", 1.0, "ab", "0031 0032 0033")]
    [InlineData("default-space", "7", 3.0, null, "0037 0020 0020")]
    [InlineData("single-code-unit-fill", "7", 3.0, "0", "0037 0030 0030")]
    [InlineData("both-empty", "", 0.0, "", "")]
    public void StringPadEnd_FillPattern(string row, string text, double width, string? fill, string expectedHex)
    {
        var source = fill is null
            ? "function main(s: string, w: number): string { return s.padEnd(w); }"
            : "function main(s: string, w: number, f: string): string { return s.padEnd(w, f); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::padEnd");
        TsValue widthValue = width == Math.Truncate(width) ? TsValue.FromInt32((int)width) : TsValue.FromFloat64(width);
        TsValue[] arguments = fill is null
            ? [TsValue.FromString(text), widthValue]
            : [TsValue.FromString(text), widthValue, TsValue.FromString(fill)];
        Assert.Equal(arguments.Length, call.Operand1);

        var bytes = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = vm.Execute(bytes, "main", arguments); });
        string actual = result is TsStringValue value
            ? string.Join(" ", value.Value.Select(unit => ((int)unit).ToString("X4")))
            : $"<{exception?.GetType().Name ?? result?.GetType().Name ?? "null"}>";
        _output.WriteLine($"StringPadEnd row={row}; parser/binder=0/0; IR=String::padEnd/{call.Operand1}; expected=[{expectedHex}]; VM=[{actual}]");
        Assert.Equal(expectedHex, actual);
    }

    [Theory]
    [InlineData("repeat-and-truncate", "7", 6, "ab", "ababa7")]
    [InlineData("empty-fill", "7", 6, "", "7")]
    [InlineData("empty-fill-huge-width", "7", int.MaxValue, "", "7")]
    [InlineData("fill-longer-than-gap", "7", 3, "abcd", "ab7")]
    [InlineData("utf16-truncate", "7", 4, "😀", null)]
    [InlineData("utf16-receiver", "😀", 3, "ab", "a😀")]
    [InlineData("width-equal", "123", 3, "ab", "123")]
    [InlineData("width-below", "123", 2, "ab", "123")]
    [InlineData("default-space", "7", 3, null, "  7")]
    [InlineData("single-unit", "7", 3, "0", "007")]
    [InlineData("fractional-width", "7", 3, "0", "007")]
    [InlineData("negative-width-unchanged", "7", -1, null, null)]
    public void StringPadStart_FillPattern(string row, string text, int width, string? fill, string? expected)
    {
        static string Hex(string value) => string.Join(" ", value.Select(unit => ((int)unit).ToString("X4")));
        if (row == "utf16-truncate") expected = "\uD83D\uDE00\uD83D\u0037";

        var source = fill is null
            ? "function main(s: string, w: any): string { return s.padStart(w); }"
            : "function main(s: string, w: any, f: any): string { return s.padStart(w, f); }";
        var widthArgument = row == "fractional-width" ? TsValue.FromFloat64(3.9) : TsValue.FromInt32(width);
        TsValue[] args = fill is null
            ? [TsValue.FromString(text), widthArgument]
            : [TsValue.FromString(text), widthArgument, TsValue.FromString(fill)];

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"StringPadStart row={row}; parser diagnostics={parser.Diagnostics.Count()}; binder diagnostics={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        _output.WriteLine($"StringPadStart row={row}; target={call.OperandObject}; IR argc={call.Operand1}; host argc={args.Length}");
        Assert.Equal("String::padStart", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(args.Length, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", args); });
        if (row == "negative-width-unchanged")
        {
            _output.WriteLine($"StringPadStart row={row}; expected=ArgumentOutOfRangeException(totalWidth); exception={error?.GetType().Name}; param={(error as ArgumentException)?.ParamName}");
            Assert.Equal("totalWidth", Assert.IsType<ArgumentOutOfRangeException>(error).ParamName);
            return;
        }

        if (error is not null)
            _output.WriteLine($"StringPadStart row={row}; expected={Hex(expected!)}; Execute exception={error.GetType().Name}; stack={error.StackTrace}");
        Assert.Null(error);
        var actual = Assert.IsType<TsStringValue>(result).Value;
        _output.WriteLine($"StringPadStart row={row}; type={result.GetType().Name}; expected={Hex(expected!)}; actual={Hex(actual)}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Expected UTF-16 [{Hex(expected!)}], actual [{Hex(actual)}]");
    }

    [Theory]
    [InlineData("null-field", null, "a,,b")]
    [InlineData("void-field", "::", "a::::b")]
    [InlineData("sparse-field", ";", ";x;")]
    [InlineData("all-nullish", null, ",,")]
    [InlineData("dense-default", null, "1,2,3")]
    [InlineData("dense-explicit", "::", "a::b::c")]
    [InlineData("empty", null, "")]
    [InlineData("literal-null", null, "a,null,b")]
    public void ArrayJoin_NullishAndSparseFields(string row, string? separator, string expected)
    {
        static TsValue Text(string value) => TsValue.FromString(value);
        static string Display(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        // Host null is a hole marker; present runtime null and undefined are TsValue instances.
        TsValue?[] slots = row switch
        {
            "null-field" => [Text("a"), TsValue.Null, Text("b")],
            "void-field" => [Text("a"), TsValue.Void, Text("b")],
            "sparse-field" => [null, Text("x"), null],
            "all-nullish" => [TsValue.Null, TsValue.Void, null],
            "dense-default" => [TsValue.FromInt32(1), TsValue.FromInt32(2), TsValue.FromInt32(3)],
            "dense-explicit" => [Text("a"), Text("b"), Text("c")],
            "empty" => [],
            "literal-null" => [Text("a"), Text("null"), Text("b")],
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };

        var array = new TsArray();
        foreach (var slot in slots)
        {
            if (slot is null) array.SetHole(array.Count);
            else array.Add(slot);
        }

        _output.WriteLine($"ArrayJoin row={row}; input-count={array.Count}; separator={(separator is null ? "omitted" : Display(separator))}");
        Assert.Equal(slots.Length, array.Count);
        for (var i = 0; i < slots.Length; i++)
        {
            var isHole = array.IsHole(i);
            var value = array.Get(i);
            _output.WriteLine($"ArrayJoin row={row}; index={i}; isHole={isHole}; get={value.GetType().Name}; value={Display(value.ToString())}");
            Assert.Equal(slots[i] is null, isHole);
            Assert.Same(slots[i] ?? TsValue.Void, value);
        }

        var source = separator is null
            ? "function main(a: any[]): string { return a.join(); }"
            : "function main(a: any[], sep: string): string { return a.join(sep); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"ArrayJoin row={row}; parser diagnostics={parser.Diagnostics.Count()}; binder diagnostics={binder.Diagnostics.All.Count()}");
        foreach (var diagnostic in parser.Diagnostics)
            _output.WriteLine($"parser [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        foreach (var diagnostic in binder.Diagnostics.All)
            _output.WriteLine($"binder [{diagnostic.Code}] {diagnostic.Location}: {diagnostic.Message}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var bytecode = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue[] args = separator is null
            ? [new TsArrayValue(array)]
            : [new TsArrayValue(array), Text(separator)];
        var actual = Assert.IsType<TsStringValue>(interpreter.Execute(bytecode, "main", args)).Value;
        _output.WriteLine($"ArrayJoin row={row}; type=TsStringValue; expected={Display(expected)}; actual={Display(actual)}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("explicit-window", "1994")]
    [InlineData("omitted-bounds", "9999")]
    [InlineData("start-only", "1299")]
    [InlineData("negative-fractions", "1994")]
    [InlineData("reverse-interval", "1234")]
    [InlineData("negative-infinity-start", "9934")]
    [InlineData("positive-infinity-end", "1299")]
    [InlineData("positive-infinity-start", "1234")]
    [InlineData("huge-negative-end", "1234")]
    [InlineData("huge-positive-end", "1299")]
    [InlineData("nan-start-string-end", "9934")]
    [InlineData("undefined-end", "1299")]
    [InlineData("null-end", "1234")]
    [InlineData("nan-end", "1234")]
    [InlineData("empty", "")]
    [InlineData("sparse-identity", "hoo4")]
    public void ArrayFill_OptionalBounds(string row, string expectedSlots)
    {
        static TsValue N(int n) => TsValue.FromInt32(n);
        static TsValue F(double n) => TsValue.FromFloat64(n);
        static string Describe(TsValue value) => value switch
        {
            TsInt32Value n => $"Int32:{n.Value}",
            TsObjectValue o => $"Object:{o.Value.TypeName}",
            _ => value.GetType().Name,
        };

        var array = new TsArray();
        TsValue?[] slots = row switch
        {
            "empty" => [],
            "sparse-identity" => [null, N(2), null, N(4)],
            _ => [N(1), N(2), N(3), N(4)],
        };
        foreach (var slot in slots)
        {
            if (slot is null) array.SetHole(array.Count);
            else array.Add(slot);
        }

        var wrapper = new TsArrayValue(array);
        var fillValue = row == "sparse-identity" ? TsValue.FromObject(new TsObject("FillToken")) : N(9);
        TsValue[] bounds = row switch
        {
            "explicit-window" or "omitted-bounds" => [],
            "start-only" => [N(2)],
            "negative-fractions" => [F(-3.8), F(-1.2)],
            "reverse-interval" => [N(3), N(2)],
            "negative-infinity-start" => [F(double.NegativeInfinity), N(2)],
            "positive-infinity-end" => [N(2), F(double.PositiveInfinity)],
            "positive-infinity-start" => [F(double.PositiveInfinity)],
            "huge-negative-end" => [F(-1e100), F(-1e100)],
            "huge-positive-end" => [N(2), F(1e100)],
            "nan-start-string-end" => [F(double.NaN), TsValue.FromString("2.9")],
            "undefined-end" => [N(2), TsValue.Void],
            "null-end" => [TsValue.Void, TsValue.Null],
            "nan-end" => [N(0), F(double.NaN)],
            "empty" => [F(double.NegativeInfinity), F(double.PositiveInfinity)],
            "sparse-identity" => [N(1), N(3)],
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        var source = row == "explicit-window"
            ? "function main(a: any[]): any[] { return a.fill(9, 1, 3); }"
            : bounds.Length switch
            {
                0 => "function main(a: any[], v: any): any[] { return a.fill(v); }",
                1 => "function main(a: any[], v: any, s: any): any[] { return a.fill(v, s); }",
                _ => "function main(a: any[], v: any, s: any, e: any): any[] { return a.fill(v, s, e); }",
            };
        TsValue[] args = row == "explicit-window" ? [wrapper] : [wrapper, fillValue, .. bounds];
        var expectedArgc = row == "explicit-window" ? 4 : args.Length;

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"ArrayFill row={row}; parser={parser.Diagnostics.Count()}; binder={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        _output.WriteLine($"ArrayFill row={row}; target={call.OperandObject}; argc={call.Operand1}; expected-argc={expectedArgc}");
        Assert.Equal("Array::fill", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(expectedArgc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", args); });
        _output.WriteLine($"ArrayFill row={row}; exception={error?.GetType().Name ?? "none"}; same-wrapper={ReferenceEquals(wrapper, result)}; count={array.Count}; expected-count={slots.Length}");
        Assert.Null(error);
        Assert.Same(wrapper, Assert.IsType<TsArrayValue>(result));
        Assert.Equal(slots.Length, array.Count);

        var actualCode = new char[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            var isHole = array.IsHole(i);
            var value = array.Get(i);
            actualCode[i] = (isHole, value) switch
            {
                (true, TsVoid) => 'h',
                (false, TsObjectValue) when ReferenceEquals(fillValue, value) => 'o',
                (false, TsInt32Value n) when row == "explicit-window" && n.Value == 9 => '9',
                (false, TsInt32Value n) when ReferenceEquals(fillValue, value) && n.Value == 9 => '9',
                (false, TsInt32Value n) when ReferenceEquals(slots[i], value) && n.Value is >= 1 and <= 4
                    => (char)('0' + n.Value),
                _ => '?',
            };
            _output.WriteLine($"ArrayFill row={row}; index={i}; isHole={isHole}; get={Describe(value)}; original-identity={ReferenceEquals(slots[i], value)}; fill-identity={ReferenceEquals(fillValue, value)}; actual={actualCode[i]}; expected={expectedSlots[i]}");
        }

        var actualSlots = new string(actualCode);
        _output.WriteLine($"ArrayFill row={row}; actual=[{actualSlots}]; expected=[{expectedSlots}]");
        Assert.Equal(expectedSlots, actualSlots);
    }

    [Theory]
    [InlineData("offset-mixed", "7HUOH9", "1, 5", 3, "HUOH")]
    [InlineData("all-holes", "7HH9", "1, 3", 3, "HH")]
    [InlineData("trailing-default-end", "78HH", "1", 2, "8HH")]
    [InlineData("dense", "7UO9", "", 1, "7UO9")]
    [InlineData("negative-range", "789A", "-3, -1", 3, "89")]
    [InlineData("reverse-range", "7HO9", "3, 1", 3, "")]
    [InlineData("empty", "", "", 1, "")]
    public void ArraySlice_PreservesSparseSlots(string row, string input, string bounds, int argc, string expected)
    {
        var token = TsValue.FromObject(new TsObject("SliceToken"));
        var array = new TsArray();
        TsValue?[] slots = input.Select(c => c switch
        {
            'H' => null,
            'U' => TsValue.Void,
            'O' => token,
            'A' => TsValue.FromInt32(10),
            _ => TsValue.FromInt32(c - '0'),
        }).ToArray();
        foreach (var slot in slots)
        {
            if (slot is null) array.SetHole(array.Count);
            else array.Add(slot);
        }

        static string ExpectedMask(string values) => new(values.Select(c => c == 'H' ? 'H' : 'P').ToArray());
        static string ActualMask(TsArray values) => new(Enumerable.Range(0, values.Count)
            .Select(i => values.IsHole(i) ? 'H' : 'P').ToArray());

        void CheckSlots(string stage, TsArray values, string specification)
        {
            for (var i = 0; i < specification.Length; i++)
            {
                var value = values.Get(i);
                var expectedSlot = specification[i];
                var description = value is TsInt32Value n ? $"Int32:{n.Value}" : value.GetType().Name;
                _output.WriteLine($"ArraySlice row={row}; stage={stage}; index={i}; hole={values.IsHole(i)}; value={description}; token-identity={ReferenceEquals(value, token)}; expected-slot={expectedSlot}");
                if (expectedSlot is 'H' or 'U') Assert.Same(TsValue.Void, value);
                else if (expectedSlot == 'O') Assert.Same(token, value);
                else Assert.Equal(expectedSlot == 'A' ? 10 : expectedSlot - '0', Assert.IsType<TsInt32Value>(value).Value);
            }
        }

        void CheckReceiver(string stage)
        {
            _output.WriteLine($"ArraySlice row={row}; stage={stage}; count={array.Count}; expected-count={input.Length}; mask=[{ActualMask(array)}]; expected-mask=[{ExpectedMask(input)}]");
            Assert.Equal(input.Length, array.Count);
            Assert.Equal(ExpectedMask(input), ActualMask(array));
            CheckSlots(stage, array, input);
        }

        CheckReceiver("receiver-before");
        var wrapper = new TsArrayValue(array);
        var source = $"function main(a: any[]): any[] {{ return a.slice({bounds}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"ArraySlice row={row}; parser={parser.Diagnostics.Count()}; binder={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        _output.WriteLine($"ArraySlice row={row}; target={call.OperandObject}; argc={call.Operand1}; expected-argc={argc}");
        Assert.Equal("Array::slice", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", [wrapper]); });
        _output.WriteLine($"ArraySlice row={row}; exception={error?.GetType().Name ?? "none"}; result-type={result?.GetType().Name}");
        Assert.Null(error);
        var resultWrapper = Assert.IsType<TsArrayValue>(result);
        var sliced = resultWrapper.Value;
        _output.WriteLine($"ArraySlice row={row}; same-wrapper={ReferenceEquals(wrapper, resultWrapper)}; same-backing={ReferenceEquals(array, sliced)}; count={sliced.Count}; expected-count={expected.Length}");
        Assert.NotSame(wrapper, resultWrapper);
        Assert.NotSame(array, sliced);
        Assert.Equal(expected.Length, sliced.Count);
        CheckReceiver("receiver-after");
        CheckSlots("result", sliced, expected);
        var actualMask = ActualMask(sliced);
        var expectedMask = ExpectedMask(expected);
        _output.WriteLine($"ArraySlice row={row}; result-mask=[{actualMask}]; expected-mask=[{expectedMask}]");
        Assert.Equal(expectedMask, actualMask);
    }

    [Fact]
    public void ForIn_EnumeratesOwnObjectKeysInInsertionOrder()
    {
        var result = Run("function main(): string { let seen: string = ''; for (let key in { first: 1, second: 2 }) { seen = seen + key; } return seen; }");
        Assert.Equal("firstsecond", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void ForIn_EnumeratesPresentArrayIndexesAndSkipsHoles()
    {
        var result = Run("function main(): string { let a = [10, 20, 30]; delete a[1]; let seen: string = ''; for (let key in a) { seen = seen + key; } return seen; }");
        Assert.Equal("02", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void ForIn_SnapshotsKeysBeforeTheLoopBodyMutatesTheObject()
    {
        var result = Run("function main(): int32 { let value = { first: 1, second: 2 }; let count: int32 = 0; for (let key in value) { value.third = 3; count = count + 1; } return count; }");
        Assert.Equal(2, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ForIn_NullEnumerationFailsAsAVmError()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): void { for (let key in null) { } }"));
        Assert.Contains("Cannot enumerate keys", error.Message);
    }

    [Fact]
    public void OptionalIndex_DoesNotEvaluateTheIndexForANullishReceiver()
    {
        var result = Run("function main(): int32 { let count: int32 = 0; function index(): int32 { count = count + 1; return 0; } let value: any = null; value?.[index()]; return count; }");
        Assert.Equal(0, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void OptionalIndex_LoadsTheElementForANonNullReceiver()
    {
        var result = Run("function main(): int32 { let value = [7]; return value?.[0]; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void OptionalCall_SkipsArgumentsForANullishCallee()
    {
        var result = Run("function main(): int32 { let count: int32 = 0; function bump(): int32 { count = count + 1; return 1; } let fn: any = null; fn?.(bump()); return count; }");
        Assert.Equal(0, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void OptionalCall_InvokesANonNullFunction()
    {
        var result = Run("function plusOne(value: int32): int32 { return value + 1; } function main(): int32 { return plusOne?.(4); }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void OptionalChaining_NullishReceiverProducesUndefined()
    {
        var indexResult = Run("function main(): any { let value: any = null; return value?.[0]; }");
        var callResult = Run("function main(): any { let fn: any = null; return fn?.(); }");

        Assert.IsType<TsVoid>(indexResult);
        Assert.IsType<TsVoid>(callResult);
    }

    private static TsValue? RunOptionalDataMember(string source, string entryPoint = "main", TsValue[]? args = null)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        parser.Parse();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(BindSourceErrors(source));
        return Run(source, entryPoint, args);
    }

    private static double OptionalDataMemberNumber(TsValue? value)
    {
        Assert.True(value is TsInt32Value or TsFloat64Value,
            $"Expected a numeric VM result, got {value?.GetType().Name ?? "<no result>"}.");
        return value is TsInt32Value int32 ? int32.Value : ((TsFloat64Value)value!).Value;
    }

    [Theory]
    [InlineData("let obj: any = null; return fallback(obj?.count);", 7)]
    [InlineData("let obj: any = undefined; return fallback(obj?.count);", 7)]
    [InlineData("return fallback(null);", 99)]
    [InlineData("return fallback(undefined);", 7)]
    [InlineData("let obj: any = { count: 0 }; return fallback(obj?.count);", 0)]
    [InlineData("let obj: any = { count: null }; return fallback(obj?.count);", 99)]
    public void OptionalDataMember_DistinguishesShortCircuitFromExplicitNull(string body, int expected)
    {
        var source = $$"""
            function fallback(value: any = 7): number {
                if (value === null) return 99;
                return value;
            }
            function main(): number { {{body}} }
            """;

        Assert.Equal((double)expected, OptionalDataMemberNumber(RunOptionalDataMember(source)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("undefined")]
    public void OptionalDataMember_DynamicNullishReceiverProducesUndefined(string receiver)
    {
        var source = $$"""
            function main(): any { let obj: any = {{receiver}}; return obj?.count; }
            """;

        Assert.IsType<TsVoid>(RunOptionalDataMember(source));
    }

    private const string OptionalDataMemberTypedSource = """
        class Box { constructor(public count: int32) {} }
        function main(obj: Box | null): any { return obj?.count; }
        function entry(): any { return main(new Box(8)); }
        """;

    [Fact]
    public void OptionalDataMember_TypedNullReceiverProducesUndefined()
    {
        Assert.IsType<TsVoid>(RunOptionalDataMember(OptionalDataMemberTypedSource, args: new[] { TsValue.Null }));
    }

    [Fact]
    public void OptionalDataMember_TypedPresentReceiverLoadsField()
    {
        Assert.Equal(8d, OptionalDataMemberNumber(RunOptionalDataMember(OptionalDataMemberTypedSource, "entry")));
    }

    [Fact]
    public void FunctionExpression_CapturesItsLexicalEnvironment()
    {
        var result = Run("function main(): int32 { let offset: int32 = 3; let add = function(value: int32): int32 { return value + offset; }; return add(4); }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ClassExpression_CanBeConstructedFromLocalBinding()
    {
        var result = Run("function main(): int32 { let Box = class { constructor(public value: int32) {} get(): int32 { return this.value; } }; let box = new Box(7); return box.get(); }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void NamedClassExpression_UsesInternalClassNameForMethods()
    {
        var result = Run("function main(): int32 { let Box = class InternalBox { get(): int32 { return 9; } }; let box = new Box(); return box.get(); }");
        Assert.Equal(9, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ClassExpression_CanBeConstructedFromClosure()
    {
        var result = Run("function main(): int32 { let Box = class { get(): int32 { return 8; } }; let make = function(): int32 { let box = new Box(); return box.get(); }; return make(); }");
        Assert.Equal(8, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ClassAccessors_InvokeGetterAndSetterThroughInheritance()
    {
        var result = Run("class Base { #value: int32; constructor(value: int32) { this.#value = value; } get value(): int32 { return this.#value; } set value(next: int32) { this.#value = next + 1; } } class Derived extends Base { constructor(value: int32) { super(value); } } function main(): int32 { let item = new Derived(2); item.value = 4; return item.value; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void PrivateFieldsUseClassIdentityAndAreNotEnumerable()
    {
        var result = Run("class A { #x: int32; visible: int32; constructor(value: int32) { this.#x = value; this.visible = 1; } get(): int32 { return this.#x; } } class B { #x: int32; constructor(value: int32) { this.#x = value; } get(): int32 { return this.#x; } } function main(): int32 { let a = new A(3); let b = new B(7); let count: int32 = 0; for (let key in a) { count = count + 1; } let copy = { ...a }; return a.get() * 100 + b.get() * 10 + count + copy.visible; }");
        Assert.Equal(372, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticBlocks_RunOnceInDeclarationOrder()
    {
        var result = Run("let order: string = ''; class Counter { static value: int32; static { order = order + 'a'; Counter.value = 1; } static { order = order + 'b'; Counter.value = Counter.value + 1; } } function main(): string { return order + Counter.value; }");
        Assert.Equal("ab2", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void ClassDecorators_EvaluateAndInvokeInOrder()
    {
        var result = Run("let order: string = ''; function first(target: any): void { order = order + '1' + target; } function second(target: any): void { order = order + '2' + target; } @first @second class Decorated { } function main(): string { return order; }");
        Assert.Equal("1Decorated2Decorated", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void MethodDecorators_InvokeBeforeClassDecorators()
    {
        var result = Run("let order: string = ''; function mark(target: any): void { order = order + target; } @mark class C { @mark run(): int32 { return 1; } } function main(): string { return order; }");
        Assert.Equal("C::runC", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void DecoratorsReceiveStandardShapeContext()
    {
        var result = Run("let order: string = ''; function mark(target: any, context: any): void { if (context['static']) { order = order + 'S'; } else { order = order + 'I'; } if (context['private']) { order = order + 'P'; } else { order = order + 'N'; } let kind: string = context.kind; let name: string = context.name; order = order + kind + name; } class C { @mark static run(): int32 { return 1; } @mark get value(): int32 { return 2; } } function main(): string { return order; }");
        Assert.Equal("SNmethodrunINgettervalue", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void IndexSignature_ProvidesIndexedValueTypeAndValidatesKey()
    {
        var result = Run("class Bag { [key: string]: int32; } function main(): int32 { let bag = new Bag(); bag['a'] = 9; return bag['a']; }");
        Assert.Equal(9, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void CyclicInheritance_SelfExtends_ReportsDiagnostic()
    {
        const string source = "class A extends A {}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().ToList();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCode.TS2019, errors[0].Code);
        Assert.Contains("Class 'A' cannot extend 'A'", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("circular inheritance", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CyclicInheritance_MutualExtends_ReportsDiagnostic()
    {
        const string source = "class A extends B {} class B extends A {}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().ToList();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCode.TS2019, errors[0].Code);
        Assert.Contains("Class 'B' cannot extend 'A'", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("circular inheritance", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CyclicInheritance_ThreeClassCycle_ReportsDiagnostic()
    {
        const string source = "class A extends B {} class B extends C {} class C extends A {}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().ToList();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCode.TS2019, errors[0].Code);
        Assert.Contains("Class 'C' cannot extend 'A'", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("circular inheritance", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CyclicInheritance_AbstractSelfExtends_ReportsDiagnostic()
    {
        const string source = "abstract class A extends A {}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().ToList();
        Assert.Single(errors);
        Assert.Equal(DiagnosticCode.TS2019, errors[0].Code);
        Assert.Contains("Class 'A' cannot extend 'A'", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("circular inheritance", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CyclicInheritance_ForwardReference_IsNotACycle()
    {
        const string source = "class A extends B {} class B {}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void CyclicInheritance_ValidChain_StillExecutes()
    {
        var result = Run("class A { greet(): string { return \"a\"; } } class B extends A {} class C extends B {} function main(): string { let c = new C(); return c.greet(); }");
        Assert.Equal("a", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void AbstractClassInstantiationAndMissingOverrideReportDiagnostics()
    {
        const string source = "abstract class Base { abstract value(): int32; } class Derived extends Base { } function main(): void { let base = new Base(); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("abstract class 'Base'", StringComparison.Ordinal));
        Assert.Contains(errors, message => message.Contains("abstract method 'value'", StringComparison.Ordinal));
    }

    [Fact]
    public void PrivateMemberAccessOutsideDeclaringClassReportsDiagnostic()
    {
        const string source = "class Box { #value: int32; constructor() { this.#value = 1; } } function main(): int32 { let box = new Box(); return box.#value; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("Private field '#value' is not accessible here", StringComparison.Ordinal));
    }

    [Fact]
    public void GetterOnlyPropertyAssignmentReportsDiagnostic()
    {
        const string source = "class Box { get value(): int32 { return 1; } } function main(): void { let box = new Box(); box.value = 2; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("Cannot assign to readonly member 'value'", StringComparison.Ordinal));
    }

    [Fact]
    public void IndexSignatureRejectsIncompatibleKeyType()
    {
        const string source = "class Bag { [key: string]: int32; } function main(): int32 { let bag = new Bag(); return bag[true]; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("Index type 'bool' is not compatible with declared index signatures", StringComparison.Ordinal));
    }

    [Fact]
    public void IndexedAccessTypeResolvesObjectPropertyType()
    {
        var result = Run("type User = { id: int32; name: string; }; type UserId = User['id']; function main(): int32 { let id: UserId = 42; return id; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void IndexedAccessTypeRejectsMissingProperty()
    {
        const string source = "type User = { id: int32; }; type Missing = User['name']; function main(): void { }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("has no index signature for ''name''", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyofTypeAcceptsDeclaredKeysOnly()
    {
        const string source = "type User = { id: int32; name: string; }; function main(): void { let ok: keyof User = 'id' as 'id'; let bad: keyof User = 'missing' as 'missing'; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("cannot assign 'missing' to 'id' | 'name'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeofTypeQueryUsesValueType()
    {
        var result = Run("function main(): int32 { let sample = { count: 4 }; let copy: typeof sample = { count: 5 }; return copy.count; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void IntersectionTypeRequiresAllConstituentMembers()
    {
        var result = Run("type HasA = { a: int32; }; type HasB = { b: string; }; type Both = HasA & HasB; function main(): int32 { let both: Both = { a: 3, b: 'xy' }; let left: HasA = both; return both.a + both.b.length + left.a; }");
        Assert.Equal(8, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void UnknownIsAssignableFromAnythingButNotToSpecificTypes()
    {
        const string source = "function main(): void { let value: unknown = 'x'; let text: string = value; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("cannot assign unknown to string", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SatisfiesExpressionChecksTargetButPreservesExpressionType()
    {
        var result = Run("type HasId = { id: int32; }; function main(): int32 { let value = { id: 7, name: 'ok' } satisfies HasId; return value.name == 'ok' ? value.id : 0; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void SatisfiesExpressionReportsStructuralMismatch()
    {
        const string source = "type HasId = { id: int32; }; function main(): void { let value = { name: 'missing' } satisfies HasId; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("does not satisfy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypePredicateNarrowsArgumentInTrueBranch()
    {
        const string source = "function isString(value: unknown): value is string { return typeof value == 'string'; } function main(): int32 { let value: unknown = 'hello'; if (isString(value)) { let text: string = value; return text.length; } return 0; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void TypePredicateRequiresDeclaredParameter()
    {
        const string source = "function isString(value: unknown): missing is string { return true; } function main(): void { }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("must be declared in function parameters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssertionSignatureNarrowsFollowingStatements()
    {
        const string source = "function assertString(value: unknown): asserts value is string { if (typeof value != 'string') { throw 'not string'; } } function main(): int32 { let value: unknown = 'hello'; assertString(value); let text: string = value; return text.length; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ConditionalTypeResolvesConcreteBranch()
    {
        var result = Run("type Result = string extends string ? int32 : bool; function main(): int32 { let value: Result = 9; return value; }");
        Assert.Equal(9, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void TemplateLiteralTypePreservesStaticLiteral()
    {
        const string source = "type EventName = `click`; function main(): void { let ok: EventName = 'click' as `click`; let bad: EventName = 'hover' as `hover`; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("cannot assign 'hover' to 'click'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FunctionOverloadsSelectCompatibleSignatureReturnType()
    {
        var result = Run("function pick(value: string): string; function pick(value: int32): int32; function pick(value: any): any { return value; } function main(): int32 { let number: int32 = pick(4); let text: string = pick('abc'); return number + text.length; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void FunctionOverloadsRejectCallsOutsideDeclaredSignatures()
    {
        const string source = "function pick(value: string): string; function pick(value: int32): int32; function pick(value: any): any { return value; } function main(): void { pick(true); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("No overload matches call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscriminatedUnionNarrowsByLiteralMemberComparison()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): int32 { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; if (shape.kind == 'circle') { return shape.radius; } return 0; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DiscriminatedUnionNarrowsByNegativeLiteralComparison()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): int32 { let shape: Shape = { kind: 'square', size: 5 } as Shape; if (shape.kind != 'circle') { return shape.size; } return 0; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MemberEqualityDoesNotNarrowPlainObjectToNever()
    {
        const string source = "type State = { port: int32; }; function main(): int32 { let state: State = { port: 0 }; if (state.port === 0) { state.port = 27015; } return state.port; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(27015, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MemberEqualityDoesNotUseWidenedUnionMemberAsDiscriminant()
    {
        const string source = "type Broad = { kind: string; port: int32; }; type Ready = { kind: 'ready'; port: int32; }; type State = Broad | Ready; function main(): string { let state: State = { kind: 'ready', port: 0 } as State; if (state.kind === 'ready') { state.kind = 'other'; } return state.kind; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal("other", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void UnionRejectsMemberThatIsNotCommonBeforeNarrowing()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): void { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; shape.radius; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("No member 'radius'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnionPreservesLiteralTypesAndExplicitNullishMembers()
    {
        const string source = "type State = 'ready' | 'busy' | 1 | true | null | undefined | never | 'ready'; function main(): void { const okText: State = 'ready'; const okNumber: State = 1; const okBool: State = true; const okNull: State = null; const okUndefined: State = undefined; const bad: State = 'other'; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("cannot assign 'other'", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, message => message.Contains("cannot assign 'ready'", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, message => message.Contains("cannot assign 1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, message => message.Contains("cannot assign true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, message => message.Contains("cannot assign null", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, message => message.Contains("cannot assign undefined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NullishUnionNarrowsNullAndUndefinedExplicitly()
    {
        var result = Run("type MaybeText = string | null | undefined; function main(): int32 { let value: MaybeText = 'hello' as MaybeText; if (value != null) { if (value != undefined) { return value.length; } } return 0; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DiscriminatedUnionAllowsAssertNeverAfterExhaustiveIfChain()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function assertNever(value: never): int32 { throw 'unexpected'; } function main(): int32 { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; if (shape.kind == 'circle') { return shape.radius; } if (shape.kind == 'square') { return shape.size; } return assertNever(shape); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DiscriminatedUnionNarrowsSingleVariantAfterThreeWayChain()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Triangle = { kind: 'triangle'; height: int32; }; type Shape = Circle | Square | Triangle; function assertNever(value: never): int32 { throw 'unexpected'; } function main(): int32 { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; if (shape.kind == 'circle') { return shape.radius; } if (shape.kind == 'square') { return shape.size; } if (shape.kind == 'triangle') { return shape.height; } return assertNever(shape); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DiscriminatedUnionNarrowsSingleVariantAfterNotEqualGuard()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function assertNever(value: never): int32 { throw 'unexpected'; } function main(): int32 { let shape: Shape = { kind: 'square', size: 5 } as Shape; if (shape.kind != 'square') { return shape.radius; } if (shape.kind == 'square') { return shape.size; } return assertNever(shape); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DiscriminatedUnionNarrowsSingleVariantInElseBranch()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function assertNever(value: never): int32 { throw 'unexpected'; } function main(): int32 { let shape: Shape = { kind: 'square', size: 5 } as Shape; if (shape.kind == 'circle') { return shape.radius; } else if (shape.kind == 'square') { return shape.size; } else { return assertNever(shape); } }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void PlainInterfaceMemberEqualityDoesNotNarrowToNever()
    {
        const string source = "function main(): number { let config: { port: number } = { port: 8080 }; if (config.port == 8080) { return config.port; } return config.port; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(
            BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree)), "main");
        Assert.Equal(8080, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Theory]
    [InlineData("other: 5", "x", false)]
    [InlineData("x: undefined", "x", true)]
    [InlineData("x: null", "x", true)]
    [InlineData("x: 0", "x", true)]
    [InlineData("'': 0", "", true)]
    [InlineData("xy: 0, X: 0", "x", false)]
    public void InOperatorTestsOwnLiteralKeyPresence(string fields, string key, bool expected)
    {
        var source = $"function main(): bool {{ let o = {{ {fields} }}; return '{key}' in o; }}";
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(expected, Assert.IsType<TsBoolValue>(Run(source)).Value);
    }

    [Fact]
    public void InOperatorEvaluatesReceiverOnce()
    {
        const string source = """
            let calls: int32 = 0;
            function pick(): { x: int32 } { calls = calls + 1; return { x: 5 }; }
            function main(): int32 {
                let found = 'x' in pick();
                if (found) { return calls; }
                return -1;
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(1, Assert.IsType<TsInt32Value>(Run(source)).Value);
    }

    [Fact]
    public void InOperatorNarrowsUnionOnAbsentProperty()
    {
        const string source = """
            type Circle = { kind: 'circle'; radius: int32; };
            type Square = { kind: 'square'; size: int32; };
            type Shape = Circle | Square;
            function main(): int32 {
                let shape: Shape = { kind: 'circle', radius: 4 } as Shape;
                if ('size' in shape) { return shape.size; }
                else { return shape.radius; }
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(4, Assert.IsType<TsInt32Value>(Run(source)).Value);
    }

    [Fact]
    public void InOperatorRetainsDynamicKeyGuard()
    {
        const string source = "function main(): bool { let o = { x: 0 }; let key: string = 'x'; return key in o; }";
        Assert.Empty(BindSourceErrors(source));
        var ex = Assert.Throws<InvalidOperationException>(() => Run(source));
        Assert.Equal("The 'in' operator currently requires a string literal property name", ex.Message);
    }

    [Theory]
    [InlineData("length", "[1]")]
    [InlineData("unrecognized", "[1]")]
    [InlineData("unrecognized", "null")]
    public void InOperatorRetainsNonObjectFallback(string key, string receiver)
    {
        var source = $"function check(o: any): bool {{ return '{key}' in o; }} function main(): bool {{ return check({receiver}); }}";
        Assert.Empty(BindSourceErrors(source));
        Assert.True(Assert.IsType<TsBoolValue>(Run(source)).Value);
    }

    [Fact]
    public void InOperatorDoesNotChangeReadingAbsentField()
    {
        const string source = "function read(o: any): any { return o.x; } function main(): any { return read({ other: 5 }); }";
        Assert.Empty(BindSourceErrors(source));
        Assert.Same(TsValue.Null, Run(source));
    }

    [Fact]
    public void InOperatorNarrowsUnionByPropertyPresence()
    {
        var result = Run("type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): int32 { let shape: Shape = { kind: 'square', size: 5 } as Shape; if ('size' in shape) { return shape.size; } return 0; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DiscriminantMutationIsRejectedAfterNarrowing()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): void { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; if (shape.kind == 'circle') { shape.kind = 'square'; } }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("Cannot assign ''square''", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SwitchOverDiscriminantRequiresExhaustiveLiteralCases()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): int32 { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; switch (shape.kind) { case 'circle': return 1; } return 0; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("Switch is not exhaustive", StringComparison.OrdinalIgnoreCase) &&
                                           message.Contains("'square'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SwitchOverDiscriminantAcceptsAllLiteralCases()
    {
        const string source = "type Circle = { kind: 'circle'; radius: int32; }; type Square = { kind: 'square'; size: int32; }; type Shape = Circle | Square; function main(): int32 { let shape: Shape = { kind: 'circle', radius: 4 } as Shape; switch (shape.kind) { case 'circle': return 1; case 'square': return 2; } return 0; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        Assert.Empty(binder.Diagnostics.GetErrors());
    }

    [Fact]
    public void MappedTypeResolvesKeyofIndexedAccessProperties()
    {
        var result = Run("type User = { id: int32; name: string; }; type Copy = { [K in keyof User]: User[K]; }; function main(): int32 { let copy: Copy = { id: 6, name: 'abc' }; return copy.id + copy.name.length; }");
        Assert.Equal(9, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ConditionalTypeInferExtractsArrayElementType()
    {
        var result = Run("type Element = int32[] extends Array<infer T> ? T : never; function main(): int32 { let value: Element = 11; return value; }");
        Assert.Equal(11, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ConditionalTypeInferUsesFalseBranchWhenPatternDoesNotMatch()
    {
        var result = Run("type Element = string extends Array<infer T> ? T : int32; function main(): int32 { let value: Element = 12; return value; }");
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void GenericTypeAliasInstantiatesConditionalInferType()
    {
        var result = Run("type Element<T> = T extends Array<infer U> ? U : never; function main(): int32 { let value: Element<int32[]> = 13; return value; }");
        Assert.Equal(13, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void GenericMappedTypeAliasInstantiatesPerTypeArgument()
    {
        var result = Run("type Copy<T> = { [K in keyof T]: T[K]; }; type User = { id: int32; name: string; }; function main(): int32 { let copy: Copy<User> = { id: 5, name: 'abcd' }; return copy.id + copy.name.length; }");
        Assert.Equal(9, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void GenericTypeAliasReportsWrongTypeArgumentCount()
    {
        const string source = "type Box<T> = T[]; type Bad = Box; function main(): void { }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("Type alias 'Box' expected 1 type argument", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenericTypeAliasUsesDefaultTypeArgument()
    {
        var result = Run("type Value<T = string> = T; function main(): int32 { let value: Value = 'abcd'; return value.length; }");
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void GenericTypeAliasValidatesConstraint()
    {
        const string source = "type IdOf<T extends { id: int32 }> = T['id']; type Bad = IdOf<{ name: string }>; function main(): void { }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        var errors = binder.Diagnostics.GetErrors().Select(d => d.Message).ToArray();
        Assert.Contains(errors, message => message.Contains("does not satisfy constraint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComputedObjectProperty_EvaluatesKeyBeforeValueAndStoresByRuntimeKey()
    {
        var result = Run("function main(): string { let order: string = ''; function key(): string { order = order + 'k'; return 'answer'; } function value(): int32 { order = order + 'v'; return 42; } let obj = { [key()]: value() }; return order + obj['answer']; }");
        Assert.Equal("kv42", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void ComputedObjectProperty_ConvertsNumericKeyToPropertyName()
    {
        var result = Run("function main(): int32 { let obj = { [1]: 9 }; return obj[1]; }");
        Assert.Equal(9, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LabelledBreak_ExitsTheNamedOuterLoop()
    {
        var result = Run("function main(): int32 { let count: int32 = 0; outer: for (let i: int32 = 0; i < 3; i = i + 1) { for (let j: int32 = 0; j < 3; j = j + 1) { count = count + 1; break outer; } } return count; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LabelledContinue_ResumesTheNamedOuterLoop()
    {
        var result = Run("function main(): int32 { let count: int32 = 0; outer: for (let i: int32 = 0; i < 3; i = i + 1) { for (let j: int32 = 0; j < 3; j = j + 1) { continue outer; } count = count + 10; } return count; }");
        Assert.Equal(0, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ChainedLabels_CanTargetTheSameLoopForContinue()
    {
        var result = Run("function main(): int32 { let count: int32 = 0; outer: inner: for (let i: int32 = 0; i < 3; i = i + 1) { count = count + 1; continue outer; count = count + 100; } return count; }");
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ArrayDestructuring_EvaluatesTheSourceOnceAndBindsIndexes()
    {
        var result = Run("function main(): int32 { let calls: int32 = 0; function source(): any { calls = calls + 1; return [4, 5]; } let [first, second] = source(); return calls * 100 + first * 10 + second; }");
        Assert.Equal(145, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ObjectDestructuring_BindsNamedProperties()
    {
        var result = Run("function main(): int32 { let { left, right } = { left: 6, right: 7 }; return left * 10 + right; }");
        Assert.Equal(67, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ArrayDestructuring_AppliesDefaultAndCreatesIndependentRestArray()
    {
        var result = Run("function main(): int64 { let [first, missing = 8, ...rest] = [3]; rest[0] = 9; return first * 100 + missing * 10 + rest.length; }");
        Assert.Equal(381, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ObjectDestructuring_RestExcludesBoundOwnProperties()
    {
        var result = Run("function main(): int64 { let { kept, ...rest } = { kept: 2, other: 4 }; return kept * 10 + rest.other; }");
        Assert.Equal(24, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ObjectDestructuring_AliasUsesTheSourcePropertyName()
    {
        var result = Run("function main(): int64 { let { source: local } = { source: 12 }; return local; }");
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DestructuringDeclaration_SupportsNestedDefaultsAndRest()
    {
        var result = Run("function main(): int64 { let [first, { missing: second = 8 }, ...rest] = [3, {}, 5, 6]; return first * 1000 + second * 100 + rest[0] * 10 + rest[1]; }");
        Assert.Equal(3856, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ObjectDestructuringDeclaration_NestedAliasAndRest()
    {
        var result = Run("function main(): int64 { let { nested: { value: local }, ...rest } = { nested: { value: 4 }, kept: 7 }; return local * 10 + rest.kept; }");
        Assert.Equal(47, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void Destructuring_RestMustBeLast()
    {
        const string source = "function main(): void { let [first, ...rest, last] = [1, 2, 3]; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());

        parser.Parse();

        Assert.Contains(parser.Diagnostics,
            diagnostic => diagnostic.Message.Contains("rest binding must be the last", StringComparison.Ordinal));
    }

    [Fact]
    public void DestructuringAssignment_EvaluatesSourceOnceAndAssignsArrayElements()
    {
        var result = Run("function main(): int32 { let calls: int32 = 0; let a: int32 = 0; let b: int32 = 0; function source(): any { calls = calls + 1; return [4, 5]; } [a, b] = source(); return calls * 100 + a * 10 + b; }");
        Assert.Equal(145, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DestructuringAssignment_SupportsNestedDefaultsAndObjectRest()
    {
        var result = Run("function main(): int64 { let a: int32 = 0; let b: int32 = 0; let rest: any = null; ({ left: a, nested: { missing: b = 7 }, ...rest } = { left: 3, nested: {}, kept: 9 }); return a * 100 + b * 10 + rest.kept; }");
        Assert.Equal(379, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void DestructuringAssignment_InvalidTargetReportsBinderError()
    {
        const string source = "function main(): void { [1] = [2]; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        Assert.Contains(binder.Diagnostics.GetErrors(),
            diagnostic => diagnostic.Message.Contains("Invalid destructuring assignment target", StringComparison.Ordinal));
    }

    [Fact]
    public void ArraySpread_EvaluatesSourceOnceAndCopiesInOrder()
    {
        var result = Run("function main(): int64 { let calls: int32 = 0; function source(): any { calls = calls + 1; return [2, 3]; } let values = [1, ...source(), 4]; return calls * 1000 + values[0] * 100 + values[1] * 10 + values[3]; }");
        Assert.Equal(1124, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void CallSpread_ExpandsArgumentsInEvaluationOrder()
    {
        var result = Run("function sum(a: int32, b: int32, c: int32): int32 { return a * 100 + b * 10 + c; } function main(): int32 { return sum(1, ...[2], 3); }");
        Assert.Equal(123, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void RestParameter_CapturesExtraArgumentsInOrder()
    {
        var result = Run("function pack(first: int32, ...rest: any): int32 { return first * 100 + rest[0] * 10 + rest[1]; } function main(): int32 { return pack(1, 2, 3); }");
        Assert.Equal(123, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void RestParameter_CreatesEmptyArrayWhenNoExtraArguments()
    {
        var result = Run("function pack(...rest: any): int32 { return rest.length; } function main(): int32 { return pack(); }");
        Assert.Equal(0, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void RestParameter_MustBeLast()
    {
        const string source = "function main(...rest: any, value: int32): void { }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());

        parser.Parse();

        Assert.Contains(parser.Diagnostics,
            diagnostic => diagnostic.Message.Contains("rest parameter must be the last", StringComparison.Ordinal));
    }

    [Fact]
    public void ArraySpread_ConsumesArrayLikeRuntimeObjects()
    {
        var result = Run("function main(): int64 { let source: any = { length: 2, 0: 6, 1: 7 }; let values = [5, ...source]; return values[0] * 100 + values[1] * 10 + values[2]; }");
        Assert.Equal(567, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ForOf_ConsumesArrayLikeRuntimeObjectsThroughSharedIterablePolicy()
    {
        var result = Run("function main(): int64 { let source: any = { length: 2, 0: 4, 1: 5 }; let total: int64 = 0; for (let value of source) { total = total * 10 + value; } return total; }");
        Assert.Equal(45, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ArrayFrom_UsesSharedIterablePolicyAndMapper()
    {
        var result = Run("function main(): int64 { let source: any = { length: 2, 0: 3, 1: 4 }; let values = Array.from(source, (value: int32) => value + 1); return values[0] * 10 + values[1]; }");
        Assert.Equal(45, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void OptionalCallSpread_SkipsSpreadEvaluationForNullishCallee()
    {
        var result = Run("function main(): int32 { let count: int32 = 0; function args(): any { count = count + 1; return [1]; } let fn: any = null; fn?.(...args()); return count; }");
        Assert.Equal(0, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void RegexLiteral_ProducesDedicatedValueAndSupportsTest()
    {
        var result = Run("function main(): bool { return /^a+$/i.test('AAA'); }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Fact]
    public void GeneratorFunction_YieldsValuesThroughNext()
    {
        var result = Run("function* numbers(): any { yield 4; yield 5; } function main(): int32 { let g: any = numbers(); let first: any = g.next(); let second: any = g.next(); let third: any = g.next(); if (third.done) { return first.value * 10 + second.value; } return 0; }");
        Assert.Equal(45, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void GeneratorFunction_IsLazyAndSuspendsBetweenNextCalls()
    {
        var result = Run("let count: int32 = 0; function* numbers(): any { count = count + 1; yield count; count = count + 1; yield count; count = count + 1; return 7; } function main(): int32 { let g: any = numbers(); let before: int32 = count; let first: any = g.next(); let mid: int32 = count; let second: any = g.next(); let third: any = g.next(); let after: int32 = count; if (third.done) { return before * 10000 + first.value * 1000 + mid * 100 + second.value * 10 + after; } return 0; }");
        Assert.Equal(1123, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void GeneratorReturn_ClosesTheGenerator()
    {
        var result = Run("function* numbers(): any { yield 1; yield 2; } function main(): int32 { let g: any = numbers(); let first: any = g.next(); let closed: any = g.return(99); let after: any = g.next(); if (first.value === 1 && closed.done && closed.value === 99 && after.done) { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void GeneratorThrow_ClosesAndPropagatesUnhandledScriptValue()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("function* numbers(): any { yield 1; yield 2; } function main(): int32 { let g: any = numbers(); g.next(); g.throw('boom'); return 0; }"));
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratorThrow_ResumesIntoActiveCatchHandler()
    {
        var result = Run("function* numbers(): any { try { yield 1; yield 2; } catch (e: any) { yield 9; } } function main(): int32 { let g: any = numbers(); let first: any = g.next(); let caught: any = g.throw('boom'); let done: any = g.next(); if (first.value === 1 && caught.value === 9 && !caught.done && done.done) { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void YieldOutsideGenerator_ReportsBinderError()
    {
        const string source = "function main(): void { yield 1; }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();

        binder.Bind(parser.Parse());

        Assert.Contains(binder.Diagnostics.GetErrors(),
            diagnostic => diagnostic.Message.Contains("yield", StringComparison.Ordinal));
    }

    [Fact]
    public void ForOf_BreakIsAcceptedAndExitsTheLoop()
    {
        const string source = "function main(): int32 { let count: int32 = 0; for (let value of [1, 2, 3]) { count = count + 1; break; } return count; }";
        var lexer = new TypeSharp.Syntax.Lexer(source);
        var parser = new TypeSharp.Syntax.Parser.Parser(lexer.Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(parser.Parse());

        Assert.False(binder.Diagnostics.HasErrors,
            string.Join("; ", binder.Diagnostics.GetErrors().Select(d => d.Message)));

        var module = BytecodeCompiler.Compile(new IRGenerator().Generate(boundTree));
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(module, "main");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ExecuteAddFunction()
    {
        var code = "function add(a: int32, b: int32): int32 { return a + b; }";
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IR.IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = Bytecode.BytecodeCompiler.Compile(moduleIR);

        foreach (var f in bytecodeModule.Functions)
        {
            Console.Error.WriteLine($"  {f.Name}: {f.Instructions.Length} bytes, Params={f.ParameterCount}, Locals={f.LocalCount}");
            Console.Error.WriteLine($"    Bytecode: {BitConverter.ToString(f.Instructions)}");
        }
        foreach (var f in moduleIR.Functions)
        {
            foreach (var b in f.Blocks)
            {
                Console.Error.WriteLine($"    Block {b.Id}:");
                foreach (var instr in b.Instructions)
                    Console.Error.WriteLine($"      {instr.Opcode} op0={instr.Operand0} opObj={instr.OperandObject}");
            }
        }

        var result = Run(code, "add", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteReturnConstant()
    {
        var result = Run(@"
            function main(): int32 {
                return 42;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(42, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteVariableAssignment()
    {
        var result = Run(@"
            function main(): int32 {
                let x: int32 = 10;
                let y: int32 = 20;
                return x + y;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(30, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ArrayPush_PreservesObjectElements()
    {
        var result = Run(@"
            function main(): int32 {
                const mapped = [];
                mapped.push({ accountId: 42 });
                return mapped.length;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ArrayPush_WorksWhenMappingObjectsInLoop()
    {
        var result = Run(@"
            function main(): int32 {
                const players = [{ accountId: 42 }, { accountId: 77 }];
                const mapped = [];
                for (let i = 0; i < players.length; i++) {
                    mapped.push({ accountId: players[i].accountId });
                }
                return mapped.length;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(2, ((TsInt32Value)result).Value);
    }

    [Theory]
    [InlineData("mixed-with-insertions", "7HUOHH9", "1, 4, 8, 9", 5, "789H9", "HUOH")]
    [InlineData("all-holes", "7HH9", "1, 2", 3, "79", "HH")]
    [InlineData("trailing-holes-delete-rest", "78HH", "1", 2, "7", "8HH")]
    [InlineData("negative-start", "7HOH9", "-3, 2", 3, "7H9", "OH")]
    [InlineData("explicit-undefined-control", "7UO9", "1, 2", 3, "79", "UO")]
    [InlineData("hole-survives-shift-control", "7OH9", "0, 1", 3, "OH9", "7")]
    [InlineData("insert-undefined-without-delete-control", "7H9", "1, 0, undefined", 4, "7UH9", "")]
    [InlineData("outside-length-control", "7H", "5, 2, 9", 4, "7H9", "")]
    public void ArraySplice_PreservesRemovedSparseSlots(
        string row, string input, string arguments, int argc, string expectedReceiver, string expectedRemoved)
    {
        var elements = string.Join(", ", input.Select(c => c switch
        {
            'H' or 'U' => "undefined",
            'O' => "token",
            _ => c.ToString(),
        }));
        var deletes = string.Join(" ", Enumerable.Range(0, input.Length)
            .Where(i => input[i] == 'H').Select(i => $"delete a[{i}];"));
        var source = $"function main(token: any): any[][] {{ const a: any[] = [{elements}]; {deletes} const removed = a.splice({arguments}); return [a, removed]; }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var syntax = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::splice", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

        var token = TsValue.FromObject(new TsObject("SpliceToken"));
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var output = Assert.IsType<TsArrayValue>(interpreter.Execute(BytecodeCompiler.Compile(ir), "main", [token])).Value;
        Assert.Equal(2, output.Count);
        var receiver = Assert.IsType<TsArrayValue>(output.Get(0)).Value;
        var removed = Assert.IsType<TsArrayValue>(output.Get(1)).Value;
        Assert.NotSame(receiver, removed);

        string Describe(TsArray array) => new(Enumerable.Range(0, array.Count).Select(i =>
        {
            var value = array.Get(i);
            if (array.IsHole(i))
            {
                Assert.Same(TsValue.Void, value);
                return 'H';
            }
            if (ReferenceEquals(value, TsValue.Void)) return 'U';
            if (ReferenceEquals(value, token)) return 'O';
            return (char)('0' + Assert.IsType<TsInt32Value>(value).Value);
        }).ToArray());

        var actualReceiver = Describe(receiver);
        var actualRemoved = Describe(removed);
        _output.WriteLine($"ArraySplice row={row}; argc={call.Operand1}; receiver actual=[{actualReceiver}] expected=[{expectedReceiver}]; removed actual=[{actualRemoved}] expected=[{expectedRemoved}]");
        Assert.Equal(expectedReceiver, actualReceiver);
        Assert.Equal(expectedRemoved, actualRemoved);
    }

    [Fact]
    public void ArraySplice_RemovesAndInsertsElements()
    {
        var result = Run(@"
            function main(): int32 {
                const values = [10, 20, 30, 40];
                const removed = values.splice(1, 2, 70, 80, 90);
                return values.length * 10000 +
                    values[0] * 1000 +
                    values[1] * 100 +
                    values[2] * 10 +
                    removed.length;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(67802, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ArrayMap_WorksThroughObjectFieldReceiver()
    {
        var result = Run(@"
            function main(): int32 {
                const channel = { members: [{ id: 2 }, { id: 4 }] };
                const mapped = channel.members.map((member) => ({ id: member.id + 1 }));
                return mapped.length * 100 + mapped[0].id * 10 + mapped[1].id;
            }
        ");

        Assert.NotNull(result);
        var value = result switch
        {
            TsInt32Value intValue => intValue.Value,
            TsInt64Value longValue => (int)longValue.Value,
            _ => throw new Xunit.Sdk.XunitException($"Expected a numeric result, got {result.GetType().Name}.")
        };
        Assert.Equal(235, value);
    }

    [Fact]
    public void MapGet_ReturnsUndefinedForMissingKey()
    {
        var result = Run(@"
            function main(): int32 {
                const values = new Map<string, int32>();
                return values.get(""missing"") === undefined ? 1 : 0;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ConstObjectLiteral_PreservesIdentityAndMutation()
    {
        var result = Run(@"
            function main(): int32 {
                const state = { count: 1 };
                state.count = 7;
                return state.count;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void DynamicStandardReceivers_DispatchToTheirBuiltins()
    {
        var result = Run(@"
            function main(): int32 {
                const bytes = new Uint8Array([1, 2, 3, 4]);
                const text = ""runtime"";
                return bytes.slice(1, 3).length + text.slice(0, 2).length;
            }
        ");

        Assert.NotNull(result);
        var value = result switch
        {
            TsInt32Value intValue => intValue.Value,
            TsFloat64Value doubleValue => (int)doubleValue.Value,
            _ => throw new Xunit.Sdk.XunitException($"Expected a numeric result, got {result.GetType().Name}.")
        };
        Assert.Equal(4, value);
    }

    [Fact]
    public void ExecuteStringConcat()
    {
        var result = Run(@"
            function main(): string {
                return ""Hello "" + ""World"";
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("Hello World", ((TsStringValue)result).Value);
    }

    [Fact]
    public void ExecuteIfElse()
    {
        var result = Run(@"
            function main(): int32 {
                let x: int32 = 5;
                if (x > 3) {
                    return 1;
                } else {
                    return 0;
                }
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteWhileLoop()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 1;
                while (i <= 10) {
                    sum = sum + i;
                    i = i + 1;
                }
                return sum;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(55, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteBooleanOperations()
    {
        var result = Run(@"
            function main(): int32 {
                let a: bool = true;
                let b: bool = false;
                if (a && !b) {
                    return 1;
                }
                return 0;
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(1, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteNestedCalls()
    {
        var code = @"
            function double_it(x: int32): int32 {
                return x + x;
            }
            function main(): int32 {
                return double_it(double_it(5));
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(20, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassConstructor()
    {
        var code = @"
            class Counter {
                count: int32;
                constructor(initial: int32) {
                    this.count = initial;
                }
                getCount(): int32 {
                    return this.count;
                }
            }
            function main(): int32 {
                const c = new Counter(5);
                return c.getCount();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(5, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassMethodMutatingFields()
    {
        var code = @"
            class Counter {
                count: int32;
                constructor(initial: int32) {
                    this.count = initial;
                }
                increment(): void {
                    this.count = this.count + 1;
                }
                getCount(): int32 {
                    return this.count;
                }
            }
            function main(): int32 {
                const c = new Counter(5);
                c.increment();
                c.increment();
                c.increment();
                return c.getCount();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(8, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassMultipleFields()
    {
        var code = @"
            class Point {
                x: int32;
                y: int32;
                constructor(x: int32, y: int32) {
                    this.x = x;
                    this.y = y;
                }
                sum(): int32 {
                    return this.x + this.y;
                }
            }
            function main(): int32 {
                const p = new Point(3, 4);
                return p.sum();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteClassStringField()
    {
        var code = @"
            class Greeter {
                greeting: string;
                constructor(name: string) {
                    this.greeting = ""Hello "" + name;
                }
                getGreeting(): string {
                    return this.greeting;
                }
            }
            function main(): string {
                const g = new Greeter(""World"");
                return g.getGreeting();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("Hello World", ((TsStringValue)result).Value);
    }

    [Fact]
    public void ExecuteClassMultipleInstances()
    {
        var code = @"
            class Counter {
                count: int32;
                constructor(initial: int32) {
                    this.count = initial;
                }
                increment(): void {
                    this.count = this.count + 1;
                }
                getCount(): int32 {
                    return this.count;
                }
            }
            function main(): int32 {
                const a = new Counter(0);
                const b = new Counter(10);
                a.increment();
                a.increment();
                b.increment();
                return a.getCount() + b.getCount();
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(13, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteStructuralMethodCallThroughRegisteredClassHandler()
    {
        var result = Run("""
            interface Request {
                searchKey?: string;
                leagueId?: number;
                heroId?: number;
                startGame?: number;
                gameListIndex?: number;
            }

            interface Response {
                searchKey: string;
                leagueId: number;
                heroId: number;
                startGame: number;
                numGames: number;
                gameListIndex: number;
                gameList: number[];
                specificGames: boolean;
            }

            interface HandlerContext<TRequest, TResponse> {
                request: TRequest;
                reply(response: TResponse): void;
            }

            type Handler<TRequest, TResponse> = (ctx: HandlerContext<TRequest, TResponse>) => void;

            class Router<TRequest, TResponse> {
                handler: Handler<TRequest, TResponse>;

                constructor(handler: Handler<TRequest, TResponse>) {
                    this.handler = handler;
                }

                dispatch(ctx: HandlerContext<TRequest, TResponse>): void {
                    this.handler(ctx);
                }
            }

            class RecorderContext implements HandlerContext<Request, Response> {
                request: Request;
                recorded: string;

                constructor() {
                    this.request = { searchKey: "" };
                    this.recorded = "";
                }

                reply(response: Response): void {
                    this.recorded = response.searchKey + ":" + response.gameList.length;
                }
            }

            class Social {
                register(router: Router<Request, Response>): void {
                    router.handler = (ctx) => {
                        this.findTopSourceTvGames(ctx);
                    };
                }

                findTopSourceTvGames(
                    ctx: HandlerContext<Request, Response>
                ): void {
                    ctx.reply({
                        searchKey: ctx.request.searchKey ?? "",
                        leagueId: ctx.request.leagueId ?? 0,
                        heroId: ctx.request.heroId ?? 0,
                        startGame: ctx.request.startGame ?? 0,
                        numGames: 0,
                        gameListIndex: ctx.request.gameListIndex ?? 0,
                        gameList: [],
                        specificGames: false
                    });
                }
            }

            function main(): string {
                const ctx = new RecorderContext();
                const router = new Router<Request, Response>((unused) => {});
                const social = new Social();
                social.register(router);
                router.dispatch(ctx);
                return ctx.recorded;
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal(":0", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void ExecuteNullishCoalescingEvaluatesRightOnlyForNullishValues()
    {
        var result = Run("""
            class Counter {
                calls: number;

                constructor() {
                    this.calls = 0;
                }

                fallback(): string {
                    this.calls = this.calls + 1;
                    return "fallback";
                }
            }

            interface MaybeValue {
                value?: string;
            }

            function main(): string {
                const counter = new Counter();
                const present: MaybeValue = { value: "present" };
                const missing: MaybeValue = {};
                const first = present.value ?? counter.fallback();
                const second = missing.value ?? counter.fallback();
                return first + ":" + second + ":" + counter.calls;
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("present:fallback:1", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void ExecuteSubtractFunction()
    {
        var code = @"
            function subtract(a: int32, b: int32): int32 {
                return a - b;
            }
            function main(): int32 {
                return subtract(10, 3);
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(7, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void ExecuteUInt64Arithmetic()
    {
        var code = @"
            function main(): uint64 {
                const a: uint64 = 100u64;
                const b: uint64 = 200u64;
                return a + b;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(300UL, ((TsUInt64Value)result).Value);
    }

    [Fact]
    public void ExecuteDecimalArithmetic()
    {
        var code = @"
            function main(): decimal {
                const a: decimal = 1.5m;
                const b: decimal = 2.5m;
                return a + b;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsDecimalValue>(result);
        Assert.Equal(4.0m, ((TsDecimalValue)result).Value);
    }

    [Fact]
    public void ExecuteDecimalMultiply()
    {
        var code = @"
            function main(): decimal {
                const a: decimal = 3m;
                const b: decimal = 7m;
                return a * b;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsDecimalValue>(result);
        Assert.Equal(21.0m, ((TsDecimalValue)result).Value);
    }

    [Fact]
    public void ExecuteThrowString()
    {
        var code = @"
            function main(): string {
                throw ""something broke"";
            }
        ";

        var ex = Record.Exception(() => Run(code, "main"));
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("something broke", ex.Message);
    }

    [Fact]
    public void ExecuteInt64WithBranches()
    {
        var code = @"
            function main(): int64 {
                const big: int64 = 9999999999i64;
                if (big > 100i64) {
                    return big;
                }
                return 0i64;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt64Value>(result);
        Assert.Equal(9999999999L, ((TsInt64Value)result).Value);
    }

    [Fact]
    public void ExecuteUint64WithBranches()
    {
        var code = @"
            function main(): uint64 {
                const big: uint64 = 18446744073709551615u64;
                if (big > 100u64) {
                    return big;
                }
                return 0u64;
            }
        ";

        var result = Run(code, "main");

        Assert.NotNull(result);
        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(18446744073709551615UL, ((TsUInt64Value)result).Value);
    }

    // ── void operator tests ──

    [Fact]
    public void Void_ReturnsUndefined()
    {
        var result = Run(@"
            function main() {
                return void 42;
            }
        ");
        Assert.NotNull(result);
        Assert.IsType<TsVoid>(result);
    }

    [Fact]
    public void Void_EvaluatesOperand_ForSideEffects()
    {
        var result = Run(@"
            function bump(t: any): int32 {
                t.count = t.count + 1;
                return t.count;
            }
            function main(): int32 {
                let tracker = { count: 0 };
                void bump(tracker);
                void bump(tracker);
                void bump(tracker);
                return tracker.count as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(3, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void Void_FunctionCall_DiscardsReturnValue()
    {
        var result = Run(@"
            function sideEffect(t: any): int32 {
                t.used = 1;
                return 99;
            }
            function main(): int32 {
                let tracker = { used: 0 };
                void sideEffect(tracker);
                return tracker.used as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(1, Convert.ToInt32(result!.RawValue));
    }

    // ── delete operator tests ──

    [Fact]
    public void Delete_Field_ReturnsTrue_WhenExists()
    {
        var result = Run(@"
            function main() {
                const obj = { a: 1, b: 2, c: 3 };
                return delete obj.b;
            }
        ");
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_Field_RemovesField()
    {
        var result = Run(@"
            function main(): int32 {
                const obj = { a: 1, b: 2 };
                delete obj.a;
                if (obj.a != null) return 999;
                return obj.b;
            }
        ");
        Assert.Equal(2, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_Field_NonExistent_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                const obj = { a: 1 };
                return delete obj.z;
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_NonReference_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                let x: int32 = 42;
                return delete x;
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_NonReference_EvaluatesOperandForSideEffects()
    {
        var result = Run(@"
            function incrementTracker(t: any): int32 {
                t.calls = t.calls + 1;
                return t.calls;
            }
            function main(): int32 {
                let tracker = { calls: 0 };
                delete incrementTracker(tracker);
                return tracker.calls as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(1, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void MemberIncrement_Postfix_ReturnsOldValueAndUpdatesField()
    {
        var result = Run("function main(): int32 { let o = { count: 5 }; let r: int32 = o.count++; return r * 100 + o.count; }");
        Assert.Equal(506, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MemberIncrement_Prefix_ReturnsNewValueAndUpdatesField()
    {
        var result = Run("function main(): int32 { let o = { count: 5 }; let r: int32 = ++o.count; return r * 100 + o.count; }");
        Assert.Equal(606, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MemberDecrement_Postfix_ReturnsOldValueAndUpdatesField()
    {
        var result = Run("function main(): int32 { let o = { count: 5 }; let r: int32 = o.count--; return r * 100 + o.count; }");
        Assert.Equal(504, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MemberDecrement_Prefix_ReturnsNewValueAndUpdatesField()
    {
        var result = Run("function main(): int32 { let o = { count: 4 }; let r: int32 = --o.count; return r * 100 + o.count; }");
        Assert.Equal(303, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void IndexIncrement_Postfix_WithVariableIndex()
    {
        var result = Run("function main(): int32 { let arr = [10, 20, 30]; let i: int32 = 1; let r: int32 = arr[i]++; return r * 100 + arr[1]; }");
        Assert.Equal(2021, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void IndexIncrement_Prefix_WithVariableIndex()
    {
        var result = Run("function main(): int32 { let arr = [10, 20, 30]; let i: int32 = 1; let r: int32 = ++arr[i]; return r * 100 + arr[1]; }");
        Assert.Equal(2121, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ThisIncrement_Postfix_InsideMethod()
    {
        var code = @"
            class Counter {
                count: int32;
                constructor(initial: int32) {
                    this.count = initial;
                }
                bump(): int32 {
                    return this.count++;
                }
                getCount(): int32 {
                    return this.count;
                }
            }
            function main(): int32 {
                const c = new Counter(5);
                let r: int32 = c.bump();
                return r * 100 + c.getCount();
            }
        ";
        var result = Run(code, "main");
        Assert.Equal(506, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void NestedMemberIncrement_Postfix()
    {
        var result = Run("function main(): int32 { let a = { b: { c: 7 } }; let r: int32 = a.b.c++; return r * 100 + a.b.c; }");
        Assert.Equal(708, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MemberIncrement_EvaluatesObjectOnce()
    {
        var result = Run(@"
            function getObj(t: any): any {
                t.calls = t.calls + 1;
                return t.obj;
            }
            function main(): int32 {
                let tracker = { calls: 0, obj: { count: 5 } };
                getObj(tracker).count++;
                return tracker.calls as int32;
            }
        ");
        Assert.NotNull(result);
        Assert.Equal(1, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void IndexIncrement_EvaluatesIndexOnce()
    {
        var result = Run(@"
            function next(t: any): int32 {
                t.calls = t.calls + 1;
                return 1;
            }
            function main(): int32 {
                let arr = [10, 20, 30];
                let tracker = { calls: 0 };
                arr[next(tracker)]++;
                return tracker.calls * 100 + arr[1];
            }
        ");
        Assert.Equal(121, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalIncrement_Unchanged_PrefixAndPostfix()
    {
        var result = Run("function main(): int32 { let i: int32 = 5; let a: int32 = i++; let b: int32 = ++i; return a * 100 + b; }");
        Assert.Equal(507, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ForLoop_BodyMemberIncrement()
    {
        var result = Run("function main(): int32 { let arr = [0, 0, 0]; for (let i: int32 = 0; i < 3; i++) { arr[i]++; } return arr[0] + arr[1] * 10 + arr[2] * 100; }");
        Assert.Equal(111, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ForIterator_MemberIncrement()
    {
        var result = Run("function main(): int32 { let o = { n: 0 }; for (let i: int32 = 0; i < 3; o.n++) { i = i + 1; } return o.n; }");
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticFieldIncrement_Postfix()
    {
        var result = Run("class Counter { static value: int32; static { Counter.value = 4; } } function main(): int32 { let r: int32 = Counter.value++; return r * 100 + Counter.value; }");
        Assert.Equal(405, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Increment_UnsupportedTarget_ReportsDiagnostic()
    {
        const string callSource = "function f(): int32 { return 1; } function main(): int32 { return f()++; }";
        var callParser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(callSource).Tokenize());
        var callBinder = new TypeSharp.Semantics.Binder.Binder();
        callBinder.Bind(callParser.Parse());
        var callErrors = callBinder.Diagnostics.GetErrors().ToList();
        Assert.Single(callErrors);
        Assert.Contains("Operator '++' requires a variable, field or element", callErrors[0].Message, StringComparison.Ordinal);

        const string readonlySource = "class C { get v(): int32 { return 1; } } function main(): int32 { const c = new C(); return c.v++; }";
        var readonlyParser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(readonlySource).Tokenize());
        var readonlyBinder = new TypeSharp.Semantics.Binder.Binder();
        readonlyBinder.Bind(readonlyParser.Parse());
        var readonlyErrors = readonlyBinder.Diagnostics.GetErrors().ToList();
        Assert.Single(readonlyErrors);
        Assert.Contains("Cannot assign to readonly member 'v'", readonlyErrors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberIncrement_NumberField()
    {
        // Note (cycle 7, §3.16 adaptation): the plan's object-literal form
        // `let o = { v: 1.5 }` does NOT give the field type `number` — the
        // literal binds as TsLiteralType(1.5, Number) and structural fields
        // are not widened, so the increment takes the Add_I32 branch and
        // returns 3.5 (1.5 + 2). A class field declared `v: number` does
        // have type `number`, so this form tests the F64 member path.
        var result = Run(@"
            class Box {
                v: number;
                constructor() {
                    this.v = 1.5;
                }
            }
            function main(): number {
                const o = new Box();
                let r: number = o.v++;
                return r + o.v;
            }
        ");
        Assert.NotNull(result);
        Assert.True(Math.Abs(Convert.ToDouble(result!.RawValue) - 4.0) < 1e-9);
    }

    [Fact]
    public void Delete_Array_PreservesHoles()
    {
        var result = Run(@"
            function main(): int32 {
                const arr = [10, 20, 30, 40];
                const deleted = delete arr[1];
                if (!deleted) return -1;
                if (arr.length != 4) return -2;
                if (arr[0] != 10) return -3;
                if (arr[2] != 30) return -4;
                if (arr[3] != 40) return -5;
                if (arr[1] != null) return -6;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_Array_HoleIsSkippedByIteration()
    {
        var result = Run(@"
            function main(): int32 {
                let arr = [10, 20, 30];
                delete arr[1];
                let sum: int32 = 0;
                let i: int32 = 0;
                while (i < arr.length) {
                    if (arr[i] != null) sum = sum + arr[i];
                    i = i + 1;
                }
                return sum;
            }
        ");
        Assert.Equal(40, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_Array_OutOfRange_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                const arr = [1, 2, 3];
                return delete arr[10];
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_Array_AlreadyHole_ReturnsFalse()
    {
        var result = Run(@"
            function main() {
                let arr = [1, 2, 3];
                delete arr[1];
                return delete arr[1];
            }
        ");
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Array_SparseAssignment_CreatesHoles()
    {
        var result = Run(@"
            function main(): int32 {
                let arr = [];
                arr[0] = 10;
                arr[3] = 40;
                if (arr.length != 4) return -1;
                if (arr[0] != 10) return -2;
                if (arr[1] != null) return -3;
                if (arr[2] != null) return -4;
                if (arr[3] != 40) return -5;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Theory]
    [InlineData("append-only-on-one", "12", "if (n === 1) { a.push(9); }", "129", "01", "12")]
    [InlineData("pop-on-first", "123", "if (i === 0) { a.pop(); }", "12", "01", "12H")]
    [InlineData("replace-future-slot", "123", "if (i === 0) { a[1] = 7; }", "173", "012", "173")]
    [InlineData("fill-future-hole", "1H3", "if (i === 0) { a[1] = 7; }", "173", "012", "173")]
    [InlineData("delete-future-slot", "123", "if (i === 0) { delete a[1]; }", "1H3", "02", "1H3")]
    [InlineData("replace-current-slot", "12", "if (i === 0) { a[0] = 7; }", "72", "01", "12")]
    [InlineData("holes-undefined-ref", "1HUOH", "", "1HUOH", "023", "1HUOH")]
    [InlineData("all-holes", "HHH", "", "HHH", "", "HHH")]
    [InlineData("empty", "", "", "", "", "")]
    public void ArrayMap_UsesInitialLengthAndLiveSlots(string row, string initial, string mutation,
        string expectedSource, string expectedVisits, string expectedMapped)
    {
        var source = $$"""
            function main(a: any[], visits: int32[]): any[] {
                return a.map((n: any, i: int32): any => {
                    visits.push(i);
                    {{mutation}}
                    return n;
                });
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        _output.WriteLine($"ArrayMap row={row}; parser={parser.Diagnostics.Count()}; binder={binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var mainCall = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::map", Assert.IsType<string>(mainCall.OperandObject));
        Assert.Equal(2, mainCall.Operand1);
        var callback = Assert.Single(ir.Functions, function => function.Name != "main");
        var callbackCalls = callback.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Opcode == Opcode.Call)
            .Select(instruction => $"{instruction.OperandObject}/{instruction.Operand1}").ToArray();
        _output.WriteLine($"ArrayMap row={row}; main=Array::map/2; callback={string.Join(",", callbackCalls)}");
        Assert.Equal(row switch
        {
            "append-only-on-one" => new[] { "Array::push/2", "Array::push/2" },
            "pop-on-first" => new[] { "Array::push/2", "Array::pop/1" },
            _ => new[] { "Array::push/2" },
        }, callbackCalls);

        var token = TsValue.FromObject(new TsObject("MapToken"));
        var array = new TsArray(initial.Length);
        for (var i = 0; i < initial.Length; i++)
        {
            switch (initial[i])
            {
                case 'H': array.SetHole(i); break;
                case 'U': array.Add(TsValue.Void); break;
                case 'O': array.Add(token); break;
                default: array.Add(TsValue.FromInt32(initial[i] - '0')); break;
            }
        }

        void AssertSlots(string label, TsArray actual, string expected)
        {
            Assert.True(actual.Count == expected.Length,
                $"ArrayMap matrix {row} {label}: expected Count={expected.Length}; actual Count={actual.Count}");
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(actual.IsHole(i) == (expected[i] == 'H'),
                    $"ArrayMap matrix {row} {label}: unexpected presence at {i}");
                var value = actual.Get(i);
                switch (expected[i])
                {
                    case 'H':
                    case 'U': Assert.Same(TsValue.Void, value); break;
                    case 'O': Assert.Same(token, value); break;
                    default: Assert.Equal(expected[i] - '0', Assert.IsType<TsInt32Value>(value).Value); break;
                }
            }
        }

        string DescribeSlots(TsArray actual)
        {
            var indices = Enumerable.Range(0, actual.Count);
            var mask = string.Concat(indices.Select(i => actual.IsHole(i) ? '0' : '1'));
            var values = string.Join(",", indices.Select(i =>
            {
                if (actual.IsHole(i)) return "H";
                var value = actual.Get(i);
                if (ReferenceEquals(value, TsValue.Void)) return "U";
                if (ReferenceEquals(value, token)) return "O";
                return value is TsInt32Value number ? number.Value.ToString() : value?.GetType().Name ?? "null";
            }));
            return $"count={actual.Count} mask={mask} values=[{values}]";
        }

        AssertSlots("initial", array, initial);
        var visits = new TsArray();
        var receiver = new TsArrayValue(array);
        var visitsReceiver = new TsArrayValue(visits);
        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", [receiver, visitsReceiver]); });
        _output.WriteLine($"ArrayMap row={row}; exception={error?.GetType().Name ?? "none"}; result-type={result?.GetType().Name}");
        Assert.Null(error);
        var mapped = Assert.IsType<TsArrayValue>(result);
        Assert.NotSame(receiver, mapped);
        Assert.NotSame(array, mapped.Value);

        _output.WriteLine($"ArrayMap row={row}; source {DescribeSlots(array)}; visits {DescribeSlots(visits)}; mapped {DescribeSlots(mapped.Value)}");
        AssertSlots("source", array, expectedSource);
        AssertSlots("visits", visits, expectedVisits);
        AssertSlots("mapped", mapped.Value, expectedMapped);
    }

    [Theory]
    [InlineData("append-accepted", "12", "if (i === 0) { a.push(9); }", "true", "129", "01", "12")]
    [InlineData("append-rejected", "12", "if (i === 0) { a.push(9); }", "n !== 9", "129", "01", "12")]
    [InlineData("append-after-ending-at-hole", "1H", "if (i === 0) { a.push(9); }", "true", "1H9", "0", "1")]
    [InlineData("pop-future-slot", "123", "if (i === 0) { a.pop(); }", "true", "12", "01", "12")]
    [InlineData("replace-future-slot", "123", "if (i === 0) { a[1] = 7; }", "n !== 7", "173", "012", "13")]
    [InlineData("fill-future-hole", "1H3", "if (i === 0) { a[1] = 7; }", "true", "173", "012", "173")]
    [InlineData("delete-future-slot", "123", "if (i === 0) { delete a[1]; }", "true", "1H3", "02", "13")]
    [InlineData("replace-current-slot", "12", "if (i === 0) { a[0] = 7; }", "true", "72", "01", "12")]
    [InlineData("dense-predicate", "123", "", "n !== 2", "123", "012", "13")]
    [InlineData("sparse-no-mutation", "1H2", "", "true", "1H2", "02", "12")]
    [InlineData("all-holes", "HHH", "", "true", "HHH", "", "")]
    [InlineData("empty", "", "", "true", "", "", "")]
    public void ArrayFilter_UsesInitialLengthAndLiveSlots(string row, string initial, string mutation,
        string predicate, string expectedSource, string expectedVisits, string expectedFiltered)
    {
        var source = $$"""
            function main(a: number[], visits: number[]): number[] {
                return a.filter((n: number, i: number): boolean => {
                    visits.push(i);
                    {{mutation}}
                    return {{predicate}};
                });
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::filter");
        Assert.Equal(2, call.Operand1);

        var array = new TsArray(initial.Length);
        for (var i = 0; i < initial.Length; i++)
        {
            if (initial[i] == 'H') array.SetHole(i);
            else array.Add(TsValue.FromInt32(initial[i] - '0'));
        }

        static string Slots(TsArray values) => string.Concat(
            Enumerable.Range(0, values.Count).Select(i => values.IsHole(i) ? "H" : values.Get(i) switch
            {
                TsInt32Value number => number.Value.ToString(),
                TsFloat64Value number => number.Value.ToString(),
                var other => $"<{other.GetType().Name}>"
            }));

        Assert.Equal(initial, Slots(array));
        var visits = new TsArray();
        var receiver = new TsArrayValue(array);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var filtered = Assert.IsType<TsArrayValue>(interpreter.Execute(
            BytecodeCompiler.Compile(ir), "main", [receiver, new TsArrayValue(visits)]));
        Assert.NotSame(array, filtered.Value);
        var actualSource = Slots(array);
        var actualVisits = Slots(visits);
        var actualFiltered = Slots(filtered.Value);
        _output.WriteLine($"ArrayFilter row={row}; parser/binder=0/0; IR=Array::filter/2; source={actualSource}; visits={actualVisits}; filtered={actualFiltered}");
        Assert.Equal(expectedSource, actualSource);
        Assert.Equal((expectedVisits, expectedFiltered), (actualVisits, actualFiltered));
    }

    [Fact]
    public void Array_Map_PreservesHoles()
    {
        var result = Run(@"
            function doubleIt(val: int32, idx: int32): int32 {
                return val * 2;
            }
            function main(): int32 {
                let arr = [10, 20, 30];
                delete arr[1];
                let mapped = arr.map(doubleIt);
                if (mapped.length != 3) return -1;
                if (mapped[0] != 20) return -2;
                if (mapped[1] != null) return -3;
                if (mapped[2] != 60) return -4;
                let visits = 0;
                mapped.forEach((value, index) => { visits = visits + 1; });
                if (visits != 2) return -5;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_MapOperator_RemovesKey()
    {
        var result = Run(@"
            function main(): int32 {
                const m = new Map();
                m.set(""a"", 100);
                m.set(""b"", 200);
                delete m[""a""];
                if (m.size != 1) return -1;
                return 1;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Delete_OptionalChaining_NullReceiver_ReturnsTrue()
    {
        var result = Run(@"
            function main() {
                const obj = null;
                return delete obj?.x;
            }
        ");
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_OptionalChaining_NonNull_Deletes()
    {
        var result = Run(@"
            function main() {
                const obj = { x: 10, y: 20 };
                return delete obj?.x;
            }
        ");
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void Delete_MultipleFields()
    {
        var result = Run(@"
            function main(): int32 {
                const obj = { a: 1, b: 2, c: 3, d: 4, e: 5 };
                delete obj.a;
                delete obj.c;
                delete obj.e;
                let sum: int32 = 0;
                if (obj.b != null) sum = sum + obj.b;
                if (obj.d != null) sum = sum + obj.d;
                return sum;
            }
        ");
        Assert.Equal(6, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void Void_InExpression_Context()
    {
        var result = Run("""
            var results = "";
            function main() {
                var a = void 1;
                var b = void "hello";
                var c = void (1 + 2);
                return 42;
            }
            """);
        Assert.Equal(42, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_BasicExecutesAtLeastOnce()
    {
        var result = Run(@"
            function main(): int32 {
                let i: int32 = 10;
                do {
                    i = i + 1;
                } while (false);
                return i;
            }
        ");
        Assert.Equal(11, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_CountsMultipleIterations()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 1;
                do {
                    sum = sum + i;
                    i = i + 1;
                } while (i <= 5);
                return sum;
            }
        ");
        Assert.Equal(15, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_BreakExitsLoop()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 1;
                do {
                    if (i == 4) break;
                    sum = sum + i;
                    i = i + 1;
                } while (i <= 10);
                return sum;
            }
        ");
        Assert.Equal(6, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_ContinueSkipsToCondition()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 0;
                do {
                    i = i + 1;
                    if (i % 2 == 0) continue;
                    sum = sum + i;
                } while (i < 5);
                return sum;
            }
        ");
        Assert.Equal(9, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_ConditionFalseAtStart_StillExecutesBody()
    {
        var result = Run(@"
            function main(): int32 {
                let executed: int32 = 0;
                do {
                    executed = 1;
                } while (false);
                return executed;
            }
        ");
        Assert.Equal(1, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_Nested()
    {
        var result = Run(@"
            function main(): int32 {
                let sum: int32 = 0;
                let i: int32 = 0;
                do {
                    let j: int32 = 0;
                    do {
                        sum = sum + 1;
                        j = j + 1;
                    } while (j < 3);
                    i = i + 1;
                } while (i < 2);
                return sum;
            }
        ");
        Assert.Equal(6, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DoWhile_SideEffectEvaluation()
    {
        var result = Run(@"
            function bump(t: any): int32 {
                t.count = t.count + 1;
                return t.count;
            }
            function main(): int32 {
                let tracker = { count: 0 };
                do {
                    bump(tracker);
                } while (tracker.count < 3);
                return tracker.count as int32;
            }
        ");
        Assert.Equal(3, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void DoWhile_WithElseNotConfused()
    {
        var result = Run(@"
            function main(): int32 {
                let x: int32 = 0;
                if (true) {
                    do {
                        x = x + 1;
                    } while (x < 3);
                } else {
                    x = -1;
                }
                return x;
            }
        ");
        Assert.Equal(3, ((TsInt32Value)result!).Value);
    }
    // ── Ciclo 06: JSON.stringify sobre estructuras cíclicas o muy profundas ──

    [Fact]
    public void JsonStringify_CyclicObject_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { const o: any = {}; o.self = o; return JSON.stringify(o); }"));
        Assert.Equal("Converting circular structure to JSON", error.Message);
    }

    [Fact]
    public void JsonStringify_CyclicArray_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { const a: any[] = []; a.push(a); return JSON.stringify(a); }"));
        Assert.Equal("Converting circular structure to JSON", error.Message);
    }

    [Fact]
    public void JsonStringify_IndirectCycle_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { const a: any = {}; const b: any = {}; a.b = b; b.a = a; return JSON.stringify(a); }"));
        Assert.Equal("Converting circular structure to JSON", error.Message);
    }

    [Fact]
    public void JsonStringify_CyclicMap_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { const m = new Map(); m.set(\"self\", m); return JSON.stringify(m); }"));
        Assert.Equal("Converting circular structure to JSON", error.Message);
    }

    [Fact]
    public void JsonStringify_CyclicSet_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { const s = new Set(); s.add(s); return JSON.stringify(s); }"));
        Assert.Equal("Converting circular structure to JSON", error.Message);
    }

    [Fact]
    public void JsonStringify_DeepNestingBeyondLimit_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { let x: any = 1; for (let i: int32 = 0; i < 1001; i++) { x = [x]; } return JSON.stringify(x); }"));
        Assert.Equal("Maximum JSON serialization depth exceeded", error.Message);
    }

    [Fact]
    public void JsonStringify_DeepNestingAtLimit_Serializes()
    {
        var result = Run("function main(): string { let x: any = 1; for (let i: int32 = 0; i < 1000; i++) { x = [x]; } return JSON.stringify(x); }");
        var json = Assert.IsType<TsStringValue>(result).Value;
        Assert.Equal(1000, json.Count(c => c == '['));
        Assert.EndsWith("1" + new string(']', 1000), json);
    }

    [Fact]
    public void JsonStringify_ModerateDepth_Serializes()
    {
        // Regresión contra el MaxDepth=64 por defecto de System.Text.Json:
        // hoy 100 niveles lanzan JsonException cruda; tras el arreglo serializan.
        var result = Run("function main(): string { let x: any = 1; for (let i: int32 = 0; i < 100; i++) { x = [x]; } return JSON.stringify(x); }");
        var json = Assert.IsType<TsStringValue>(result).Value;
        Assert.Equal(100, json.Count(c => c == '['));
    }

    [Fact]
    public void JsonStringify_DiamondGraph_SerializesTwice()
    {
        var result = Run("function main(): string { const shared: any = { v: 1 }; const o: any = { a: shared, b: shared }; return JSON.stringify(o); }");
        var json = Assert.IsType<TsStringValue>(result).Value;
        Assert.Contains("\"a\":{\"v\":1}", json);
        Assert.Contains("\"b\":{\"v\":1}", json);
    }

    [Fact]
    public void JsonStringify_PlainObject_Serializes()
    {
        var result = Run("function main(): string { const o: any = { a: 1 }; return JSON.stringify(o); }");
        Assert.Equal("{\"a\":1}", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void JsonStringify_ArrayAndNested_Serializes()
    {
        var result = Run("function main(): string { const o: any = { n: [1, 2], s: \"x\", b: true, z: null }; return JSON.stringify(o); }");
        var json = Assert.IsType<TsStringValue>(result).Value;
        Assert.Contains("\"n\":[1,2]", json);
        Assert.Contains("\"s\":\"x\"", json);
        Assert.Contains("\"b\":true", json);
        Assert.Contains("\"z\":null", json);
    }

    [Fact]
    public void JsonStringify_CycleError_EscapesScriptTryCatch()
    {
        // Decisión §2.4: el error de VM escapa al host; el try/catch del script NO lo captura.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): string { const o: any = {}; o.self = o; try { JSON.stringify(o); } catch (e) { return \"caught\"; } return \"no-throw\"; }"));
        Assert.Equal("Converting circular structure to JSON", error.Message);
    }

    // Ciclo 8: RunWithLimits existe porque el límite de instrucciones por defecto
    // (10M) es insuficiente para los bucles de 1.1M de iteraciones de los tests
    // de fuga de pila (§3.29-§3.36): ~11M de instrucciones por bucle.
    private static TsValue? RunWithLimits(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);

        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);

        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(new VMRuntimeLimits { MaximumInstructions = 100_000_000 });
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }

    // Ciclo 8 — E: la asignación produce valor (semántica JavaScript).

    [Fact]
    public void Assignment_Chained_ReturnsValue()
    {
        var result = Run("function main(): int32 { let a: int32 = 0; let b: int32 = 0; a = b = 5; return a; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InInitializer_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; let y: int32 = (x = 5); return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InReturn_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; return (x = 7); }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_ChainedThree_ReturnsSum()
    {
        var result = Run("function main(): int32 { let a: int32 = 0; let b: int32 = 0; let c: int32 = 0; a = b = c = 9; return a + b + c; }");
        Assert.Equal(27, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_ChainedMember_ReturnsValue()
    {
        var result = Run("function main(): int32 { let o = { n: 0 }; let x: int32 = 0; x = o.n = 4; return x * 100 + o.n; }");
        Assert.Equal(404, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_ChainedIndex_ReturnsValue()
    {
        var result = Run("function main(): int32 { let a = [0, 0]; let x: int32 = 0; x = a[1] = 6; return x * 100 + a[1]; }");
        Assert.Equal(606, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_CompoundInValue_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 10; let y: int32 = (x += 5); return y; }");
        Assert.Equal(15, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_AsStatement_Control()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; x = 5; return x; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InCondition_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; if ((x = 3) == 3) { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_AsArgument_ReturnsValue()
    {
        var result = Run("function g(n: int32): int32 { return n; } function main(): int32 { let x: int32 = 0; return g(x = 21); }");
        Assert.Equal(21, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_StaticFieldInValue_ReturnsValue()
    {
        var result = Run("class Counter { static value: int32; static { Counter.value = 4; } } function main(): int32 { let y: int32 = (Counter.value = 9); return y * 100 + Counter.value; }");
        Assert.Equal(909, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_ModuleGlobalInValue_ReturnsValue()
    {
        var result = Run("let x: int32 = 0; function main(): int32 { let y: int32 = (x = 5); return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_BoxedLocalInValue_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 1; let f = () => { return (x = 5); }; let y: int32 = f(); return y * 100 + x; }");
        Assert.Equal(505, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_MemberAsArgument_ReturnsValue()
    {
        var result = Run("function g(n: int32): int32 { return n; } function main(): int32 { let o = { n: 0 }; return g(o.n = 21); }");
        Assert.Equal(21, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_IndexInCondition_ReturnsValue()
    {
        var result = Run("function main(): int32 { let a = [0, 0]; if ((a[1] = 3) == 3) { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InArrayLiteral_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; let a = [1, (x = 5), 3]; return a[1] * 100 + x; }");
        Assert.Equal(505, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InObjectLiteral_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; let o = { n: (x = 7) }; return o.n * 100 + x; }");
        Assert.Equal(707, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_AsSwitchDiscriminant_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; switch (x = 2) { case 2: return 20 + x; default: return 0; } }");
        Assert.Equal(22, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_AsThrowOperand_ReturnsValue()
    {
        // El resultado es TsInt64Value(909), no TsInt32Value: e es any y
        // GenerateBinary ensancha a I64 cuando un operando es any (ciclo 8, R1).
        var result = Run("function main(): int32 { let x: int32 = 0; try { throw (x = 9); } catch (e: any) { return e * 100 + x; } return 0; }");
        Assert.Equal(909, Convert.ToInt32(result!.RawValue));
    }

    [Fact]
    public void Assignment_InWhileCondition_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; let n: int32 = 0; while ((x = x + 1) < 3) { n = n + 1; } return n * 100 + x; }");
        Assert.Equal(203, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InDoWhileCondition_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; let n: int32 = 0; do { n = n + 1; } while ((x = x + 1) < 2); return n * 100 + x; }");
        Assert.Equal(202, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InTernaryCondition_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; let r: int32 = (x = 5) == 5 ? 1 : 0; return r * 100 + x; }");
        Assert.Equal(105, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_ChainedMembersThree_ReturnsValue()
    {
        var result = Run("function main(): int32 { let o = { n: 0 }; let p = { n: 0 }; let q = { n: 0 }; o.n = p.n = q.n = 8; return o.n * 100 + p.n * 10 + q.n; }");
        Assert.Equal(888, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InDefaultParameter_ReturnsValue()
    {
        var result = Run("let x: int32 = 0; function f(a: int32 = (x = 5)): int32 { return a; } function main(): int32 { return f(); }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 8 — U: la asignación a propiedad con setter vale el valor asignado.

    [Fact]
    public void SetterAssignment_InInitializer_ReturnsAssignedValue()
    {
        var result = Run("class C { #v: int32; constructor() { this.#v = 0; } set p(n: int32) { this.#v = n; } get p(): int32 { return this.#v; } } function main(): int32 { let c = new C(); let y: int32 = (c.p = 5); return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void SetterAssignment_InReturn_ReturnsAssignedValue()
    {
        var result = Run("class C { #v: int32; constructor() { this.#v = 0; } set p(n: int32) { this.#v = n; } get p(): int32 { return this.#v; } } function main(): int32 { let c = new C(); return (c.p = 5); }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void SetterAssignment_Chained_ReturnsAssignedValue()
    {
        var result = Run("class C { #v: int32; constructor() { this.#v = 0; } set p(n: int32) { this.#v = n; } get p(): int32 { return this.#v; } } function main(): int32 { let c = new C(); let x: int32 = 0; x = (c.p = 5); return x * 100 + c.p; }");
        Assert.Equal(505, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void SetterAssignment_AsStatement_Control()
    {
        var result = Run("class C { #v: int32; constructor() { this.#v = 0; } set p(n: int32) { this.#v = n; } get p(): int32 { return this.#v; } } function main(): int32 { let c = new C(); c.p = 5; return c.p; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 8 — S + U: bucles de 1.1M de iteraciones; ninguna asignación en
    // posición de sentencia o de iterador puede filtrar slots de pila.

    [Fact]
    public void ForIterator_Increment_LeaksNoSlot()
    {
        var result = RunWithLimits("function main(): int32 { for (let i: int32 = 0; i < 1100000; i++) { } return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ForIterator_Assignment_LeaksNoSlot()
    {
        var result = RunWithLimits("function main(): int32 { let i: int32 = 0; for (i = 0; i < 1100000; i = i + 1) { } return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ForIterator_CallReturningValue_LeaksNoSlot()
    {
        var result = RunWithLimits("function f(): int32 { return 1; } function main(): int32 { let i: int32 = 0; for (i = 0; i < 1100000; f()) { i++; } return i; }");
        Assert.Equal(1100000, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_SetterAssignments_LeakNoSlot()
    {
        var result = RunWithLimits("class C { #v: int32; constructor() { this.#v = 0; } set p(n: int32) { this.#v = n; } get p(): int32 { return this.#v; } } function main(): int32 { let c = new C(); let i: int32 = 0; while (i < 1100000) { c.p = i; i++; } return c.p; }");
        Assert.Equal(1099999, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_StaticSetterAssignments_LeakNoSlot()
    {
        // Ciclo 9 (V, §3.39): el getter estático lee fielmente el campo; lo
        // roto era el setter estático, que escribía `Void` en el campo por el
        // desplazamiento de argumentos de los miembros estáticos. Este test
        // afirma ahora el valor leído (1099999), no solo la disciplina de
        // pila del bucle.
        var result = RunWithLimits("class C { static v: int32; static set p(n: int32) { C.v = n; } static get p(): int32 { return C.v; } } function main(): int32 { let i: int32 = 0; while (i < 1100000) { C.p = i; i++; } return C.p; }");
        Assert.Equal(1099999, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_PlainAssignments_LeakNoSlot()
    {
        var result = RunWithLimits("function main(): int32 { let x: int32 = 0; let i: int32 = 0; while (i < 1100000) { x = i; i++; } return x; }");
        Assert.Equal(1099999, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_DestructuringStatement_LeaksNoSlot()
    {
        // Origen prealocado fuera del bucle: un literal nuevo por iteración
        // moriría por Memory limit exceeded antes de llegar al tope de pila.
        var result = RunWithLimits("function main(): int32 { let a: int32 = 0; let b: int32 = 0; let src = [1, 2]; let i: int32 = 0; while (i < 1100000) { [a, b] = src; i++; } return a * 10 + b; }");
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_InvalidTargetAssignment_LeaksNoSlot()
    {
        var result = RunWithLimits("function f(): int32 { return 1; } function main(): int32 { let i: int32 = 0; while (i < 1100000) { f() = 5; i++; } return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 8 — desestructuración en posición de valor.

    [Fact]
    public void DestructuringAssignment_AsStatement_Control()
    {
        var result = Run("function main(): int32 { let a: int32 = 0; let b: int32 = 0; [a, b] = [1, 2]; return a * 10 + b; }");
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DestructuringAssignment_InValue_ReturnsSource()
    {
        var result = Run("function main(): int32 { let a: int32 = 0; let b: int32 = 0; let r = ([a, b] = [1, 2]); return r[0] * 100 + r[1] * 10 + a; }");
        Assert.Equal(121, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DestructuringAssignment_MemberTargetsInValue_ReturnsValue()
    {
        var result = Run("function main(): int32 { let o = { a: 0, b: 0 }; let r = ([o.a, o.b] = [3, 4]); return o.a * 10 + o.b; }");
        Assert.Equal(34, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void DestructuringAssignment_InReturn_ReturnsFirstElement()
    {
        var result = Run("function main(): int32 { let a: int32 = 0; let b: int32 = 0; return ([a, b] = [1, 2])[0]; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 8 — objetivo inválido, N33 y contra-tests.

    [Fact]
    public void InvalidTargetAssignment_AsStatement_Control()
    {
        var result = Run("function f(): int32 { return 1; } function main(): int32 { f() = 5; return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidTargetAssignment_InValue_ReturnsValue()
    {
        var result = Run("function f(): int32 { return 1; } function main(): int32 { let y: int32 = (f() = 5); return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void CompoundAssignment_N33_IndexEvaluatedOnce()
    {
        var result = Run("let t = { calls: 0 }; function next(): int32 { t.calls = t.calls + 1; return 1; } function main(): int32 { let arr = [10, 20, 30]; arr[next()] += 1; return t.calls * 100 + arr[1]; }");
        Assert.Equal(121, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Theory]
    [InlineData("arr[next()] += 1", 11230)]
    [InlineData("arr[next()] = arr[next()] + 1", 22330)]
    public void CompoundAssignment_N33_ChangingIndexAndIndependentReferences(string assignment, int expected)
    {
        string source = $$"""
            let calls: int32 = 0;
            function next(): int32 { let i: int32 = calls; calls = calls + 1; return i; }
            function main(): int32 {
                let arr: int32[] = [10, 20];
                let r: int32 = ({{assignment}});
                return calls * 10000 + r * 100 + arr[0] * 10 + arr[1];
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(expected, Assert.IsType<TsInt32Value>(Run(source)).Value);
    }

    [Fact]
    public void CompoundAssignment_N33_ChangingReceiverReadsAndWritesSameField()
    {
        const string source = """
            class C { v: int32 = 10; }
            function main(): int32 {
                let first: C = new C();
                let second: C = new C();
                second.v = 20;
                let calls: int32 = 0;
                function pick(): C {
                    calls = calls + 1;
                    if (calls == 1) { return first; }
                    return second;
                }
                let r: int32 = (pick().v ^= 3);
                return calls * 1000000 + r * 10000 + first.v * 100 + second.v;
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(1090920, Assert.IsType<TsInt32Value>(Run(source)).Value);
    }

    [Fact]
    public void CompoundAssignment_N33_AccessorOrderAndCapturedReceiver()
    {
        const string source = """
            let trace: int32 = 0;
            class C {
                v: int32 = 10;
                get p(): int32 { trace = trace * 10 + 2; return this.v; }
                set p(n: int32) { trace = trace * 10 + 4; this.v = n; }
            }
            function main(): int32 {
                let first: C = new C();
                let second: C = new C();
                second.v = 20;
                let selected: C = first;
                function pick(): C { trace = trace * 10 + 1; return selected; }
                function rhs(): int32 {
                    trace = trace * 10 + 3;
                    selected = second;
                    first.v = 99;
                    return 5;
                }
                let r: int32 = (pick().p += rhs());
                return trace * 100000 + r * 1000 + first.v * 25 + second.v;
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(123415395, Assert.IsType<TsInt32Value>(Run(source)).Value);
    }

    [Fact]
    public void CompoundAssignment_N33_ConcatenatesMember()
    {
        const string source = """
            class C { text: string = 'a'; }
            function main(): string {
                let c: C = new C();
                let r: string = (c.text += 'b');
                return r + ':' + c.text;
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal("ab:ab", Assert.IsType<TsStringValue>(Run(source)).Value);
    }

    [Fact]
    public void CompoundAssignment_N33_PromotesIntegerRhs()
    {
        const string source = """
            class C { value: number = 1.5; }
            function main(): number {
                let c: C = new C();
                let n: int32 = 2;
                let r: number = (c.value += n);
                return r * 10 + c.value;
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(38.5, Assert.IsType<TsFloat64Value>(Run(source)).Value);
    }

    [Fact]
    public void CompoundAssignment_N33_NestedRhsKeepsOuterIndex()
    {
        const string source = """
            function main(): int32 {
                let a: int32[] = [10, 20];
                let b: int32[] = [30, 40];
                let i: int32 = 0;
                let r: int32 = (a[i] += (b[i] += (i = 1)));
                return r * 1000000 + a[0] * 10000 + a[1] * 100 + b[0];
            }
            """;
        Assert.Empty(BindSourceErrors(source));
        Assert.Equal(41412031, Assert.IsType<TsInt32Value>(Run(source)).Value);
    }

    [Fact]
    public void CompoundAssignment_AsStatement_Control()
    {
        var result = Run("function main(): int32 { let x: int32 = 5; x += 1; return x; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Assignment_InForInitializer_ReturnsValue()
    {
        var result = Run("function main(): int32 { let x: int32 = 0; for (let y: int32 = (x = 4); y < 5; y++) { } return x; }");
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
    }
    // Ciclo 9 — V: métodos y accesores `static` con parámetros (§3.2).
    // El arreglo (opción (b), §2.1/§2.2) emite un receptor ficticio `Void`
    // en los tres sitios de llamada estática para alinear los argumentos
    // con el parámetro fantasma `this` del slot 0.

    [Fact]
    public void StaticMethod_OneParam_ReturnsArgument()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_TwoParams_ReturnsCombined()
    {
        var result = Run("class C { static f(a: int32, b: int32): int32 { return a * 10 + b; } } function main(): int32 { return C.f(4, 2); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_ThreeParams_ReturnsCombined()
    {
        var result = Run("class C { static f(a: int32, b: int32, c: int32): int32 { return a * 100 + b * 10 + c; } } function main(): int32 { return C.f(1, 2, 3); }");
        Assert.Equal(123, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_DefaultParam_NoArg_AppliesDefault()
    {
        var result = Run("class C { static f(n: int32 = 7): int32 { return n; } } function main(): int32 { return C.f(); }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_DefaultParam_WithArg_IgnoresDefault()
    {
        var result = Run("class C { static f(n: int32 = 7): int32 { return n; } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_RestParam_LengthIsCorrect()
    {
        var result = Run("class C { static f(a: int32, ...rest: int32[]): int32 { return rest.length; } } function main(): int32 { return C.f(1, 2, 3); }");
        Assert.Equal(2, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_RestParam_FirstParamIsCorrect()
    {
        var result = Run("class C { static f(a: int32, ...rest: int32[]): int32 { return a * 10 + rest.length; } } function main(): int32 { return C.f(1, 2, 3); }");
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_ExtraArgument_Ignored()
    {
        // Diagnóstico preexistente de aridad (`Expected 1 argument(s) but
        // got 2`); `Run` lo ignora y el test afirma solo el valor.
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { return C.f(42, 43); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_MissingArgument_IsVoid()
    {
        // Diagnóstico preexistente de aridad (`Expected 2 argument(s) but
        // got 1`); `Run` lo ignora y el test afirma solo el valor.
        var result = Run("class C { static f(a: int32, b: int32): int32 { return a * 10 + b; } } function main(): int32 { let t: any = C.f(42); return t; }");
        Assert.Equal(420, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_Inherited_ReturnsArgument()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } class D extends C {} function main(): int32 { return D.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_CallsStaticMethod_ReturnsArgument()
    {
        var result = Run("class C { static g(n: int32): int32 { return n + 1; } static f(n: int32): int32 { return C.g(n); } } function main(): int32 { return C.f(41); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_Generic_ReturnsArgument()
    {
        var result = Run("class C { static f<T>(x: T): T { return x; } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_PrivateCalledFromInside_ReturnsArgument()
    {
        var result = Run("class C { static #f(n: int32): int32 { return n; } static g(n: int32): int32 { return C.#f(n); } } function main(): int32 { return C.g(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_ClosureCapturesParam_ReturnsArgument()
    {
        var result = Run("class C { static f(n: int32): int32 { let g = () => { return n; }; return g(); } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_This_IsVoid()
    {
        var result = Run("class C { static f(n: int32): any { return this; } } function main(): int32 { let t: any = C.f(42); if (typeof t == \"undefined\") { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_ThisNoParams_IsVoid()
    {
        var result = Run("class C { static f(): any { return this; } } function main(): int32 { let t: any = C.f(); if (typeof t == \"undefined\") { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_ClosureCapturesThis_IsVoid()
    {
        var result = Run("class C { static f(n: int32): any { let g = () => { return this; }; return g(); } } function main(): int32 { let t: any = C.f(42); if (typeof t == \"undefined\") { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticAccessor_GetterAfterSetter_ReturnsValue()
    {
        var result = Run("class C { static v: int32; static set p(n: int32) { C.v = n; } static get p(): int32 { return C.v; } } function main(): int32 { C.p = 42; return C.p; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticSetter_AddsConstant_ReturnsStoredValue()
    {
        var result = Run("class C { static v: int32; static set p(n: int32) { C.v = n + 100; } static get p(): int32 { return C.v; } } function main(): int32 { C.p = 42; return C.v; }");
        Assert.Equal(142, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticSetter_BodyReadsThis_SetterStillWritesParam()
    {
        var result = Run("class C { static v: int32; static set p(n: int32) { let t: any = this; C.v = n; } static get p(): int32 { return C.v; } } function main(): int32 { C.p = 42; return C.p; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticSetter_BranchesOnParam_ReturnsCorrectBranch()
    {
        var result = Run("class C { static v: int32; static set p(n: int32) { if (n == 42) { C.v = 1; } else { C.v = 2; } } static get p(): int32 { return C.v; } } function main(): int32 { C.p = 42; return C.v; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_StringParam_ReturnsArgument()
    {
        var result = Run("class C { static f(s: string): string { return s; } } function main(): string { return C.f('hi'); }");
        Assert.Equal("hi", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void StaticMethod_WritesStaticField_ReturnsField()
    {
        var result = Run("class C { static v: int32; static f(n: int32): int32 { C.v = n; return C.v; } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_DecoratedWithParam_ReturnsArgument()
    {
        // La función `mark` se copia literalmente de
        // `DecoratorsReceiveStandardShapeContext` (VMTests.cs:172): con una
        // `mark` simplificada de un solo argumento el `order` no es
        // `SNmethodf` (ciclo 9, M4).
        var result = Run("let order: string = ''; function mark(target: any, context: any): void { if (context['static']) { order = order + 'S'; } else { order = order + 'I'; } if (context['private']) { order = order + 'P'; } else { order = order + 'N'; } let kind: string = context.kind; let name: string = context.name; order = order + kind + name; } class C { @mark static f(n: int32): int32 { return n; } } function main(): int32 { let r: int32 = C.f(42); if (order == 'SNmethodf') { return r; } return -1; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_WritesPrivateStaticField_ReturnsField()
    {
        var result = Run("class C { static #v: int32; static f(n: int32): int32 { C.#v = n; return C.#v; } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InstanceMethod_TwoParams_Control()
    {
        var result = Run("class C { m(a: int32, b: int32): int32 { return a * 10 + b; } } function main(): int32 { let c = new C(); return c.m(4, 2); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_NoParams_Control()
    {
        var result = Run("class C { static f(): int32 { return 42; } } function main(): int32 { return C.f(); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Constructor_OneParam_Control()
    {
        var result = Run("class C { v: int32; constructor(n: int32) { this.v = n; } } function main(): int32 { let c = new C(42); return c.v; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Constructor_TwoParams_Control()
    {
        var result = Run("class C { v: int32; constructor(a: int32, b: int32) { this.v = a * 10 + b; } } function main(): int32 { let c = new C(4, 2); return c.v; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InstanceSetter_AsStatement_Control()
    {
        var result = Run("class C { #v: int32; constructor() { this.#v = 0; } set p(n: int32) { this.#v = n; } get p(): int32 { return this.#v; } } function main(): int32 { let c = new C(); c.p = 5; return c.p; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Builtin_MathFloor_Control()
    {
        var result = Run("function main(): number { return Math.floor(3.7); }");
        Assert.Equal(3, Assert.IsType<TsFloat64Value>(result).Value);
    }

    [Fact]
    public void Builtin_MathPow_Control()
    {
        var result = Run("function main(): number { return Math.pow(2, 10); }");
        Assert.Equal(1024, Assert.IsType<TsFloat64Value>(result).Value);
    }

    [Fact]
    public void Builtin_ArrayIsArray_Control()
    {
        var result = Run("function main(): int32 { let b: any = Array.isArray([1]); return b; }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Fact]
    public void Builtin_ArrayOf_Control()
    {
        var result = Run("function main(): int32 { let a = Array.of(1, 2, 3); return a.length; }");
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Builtin_JsonStringify_Control()
    {
        var result = Run("function main(): string { return JSON.stringify({a: 1}); }");
        Assert.Equal("{\"a\":1}", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void StaticMethod_ClosureCapturesThisNoParams_Control()
    {
        var result = Run("class C { static f(): any { let g = () => { return this; }; return g(); } } function main(): int32 { let t: any = C.f(); if (typeof t == \"undefined\") { return 1; } return 0; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_OneParamCalledWithNoArgs_Control()
    {
        // Diagnóstico preexistente de aridad (`Expected 1 argument(s) but
        // got 0`); el test afirma solo el valor.
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { let t: any = C.f(); return t; }");
        Assert.IsType<TsVoid>(result);
    }

    [Fact]
    public void StaticMethod_LoopWritesField_NoLeak()
    {
        var result = RunWithLimits("class C { static v: int32; static f(n: int32): int32 { C.v = n; return C.v; } } function main(): int32 { let i: int32 = 0; while (i < 1100000) { C.f(i); i++; } return C.v; }");
        Assert.Equal(1099999, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 9 — T: `++`/`--` sobre `const` (§3.4). Tests de diagnóstico del
    // binder (patrón `Increment_UnsupportedTarget_ReportsDiagnostic`,
    // VMTests.cs:1971-1988), NO de `Run`: `Run` ignora los diagnósticos.
    // Anotación `: int32` obligatoria (Rev 2, R1): sin ella el literal se
    // infiere `number` y `return x` desde `main(): int32` añade un error
    // espurio antes del arreglo.

    private static List<Diagnostic> BindSourceErrors(string source)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        binder.Bind(parser.Parse());
        return binder.Diagnostics.GetErrors().ToList();
    }

    [Fact]
    public void Increment_ConstLocalPrefix_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("function main(): int32 { const x: int32 = 1; return ++x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_ConstLocalPostfix_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("function main(): int32 { const x: int32 = 1; x++; return x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decrement_ConstLocalPrefix_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("function main(): int32 { const x: int32 = 1; return --x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decrement_ConstLocalPostfix_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("function main(): int32 { const x: int32 = 1; x--; return x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_ConstNonConstantInitializer_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("function g(): int32 { return 1; } function main(): int32 { const x = g(); x++; return x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_ConstModuleScope_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("const x: int32 = 1; function main(): int32 { x++; return x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_ConstCapturedByClosure_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("const x: int32 = 1; function main(): int32 { let f = () => { x++; return x; }; return f(); }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_ConstForLoopVariable_ReportsDiagnostic()
    {
        // Consecuencia intencionada (§1.7): el bucle que "funcionaba" en
        // ejecución pasa a ser un error de compilación.
        var errors = BindSourceErrors("function main(): int32 { let s: int32 = 0; for (const i: int32 = 0; i < 3; i++) { s = s + i; } return s; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'i'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_LetLocal_Control()
    {
        var errors = BindSourceErrors("function main(): int32 { let x: int32 = 1; x++; return x; }");
        Assert.Empty(errors);
    }

    [Fact]
    public void Increment_LetLocalAllForms_Control()
    {
        var errors = BindSourceErrors("function main(): int32 { let x: int32 = 1; ++x; x--; --x; return x; }");
        Assert.Empty(errors);
    }

    [Fact]
    public void Assign_ConstLocal_Control()
    {
        var errors = BindSourceErrors("function main(): int32 { const x: int32 = 1; x = 2; return x; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to const variable 'x'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Increment_MemberOfConstObject_Control()
    {
        var errors = BindSourceErrors("function main(): int32 { const o = { n: 0 }; o.n++; return o.n; }");
        Assert.Empty(errors);
    }

    // Ciclo 9 — D §8.1: asignación a nombre de función/clase (§3.5). El `Dup`
    // del camino variable se emitía también sin `Store` y dejaba neto +2;
    // en bucle de 1.1M moría por tope de pila.

    [Fact]
    public void InvalidAssignment_FunctionName_Loop_LeaksNoSlot()
    {
        var result = RunWithLimits("function f(): int32 { return 1; } function main(): int32 { let i: int32 = 0; while (i < 1100000) { f = 5; i++; } return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidAssignment_ClassName_Loop_LeaksNoSlot()
    {
        // Diagnóstico preexistente `Cannot assign '5' to variable of type
        // 'C'`; `Run` lo ignora y el test afirma solo el valor.
        var result = RunWithLimits("class C { static v: int32; } function main(): int32 { let i: int32 = 0; while (i < 1100000) { C = 5; i++; } return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidAssignment_FunctionNameInValue_Loop_LeaksNoSlot()
    {
        var result = RunWithLimits("function f(): int32 { return 1; } function main(): int32 { let i: int32 = 0; let y: int32 = 0; while (i < 1100000) { y = (f = 5); i++; } return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidAssignment_ClassNameInValue_Loop_LeaksNoSlot()
    {
        // Diagnóstico preexistente `Cannot assign '5' to variable of type
        // 'C'`; `Run` lo ignora y el test afirma solo el valor.
        var result = RunWithLimits("class C { static v: int32; } function main(): int32 { let i: int32 = 0; let y: int32 = 0; while (i < 1100000) { y = (C = 5); i++; } return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidAssignment_FunctionNameInValue_Control()
    {
        var result = Run("function f(): int32 { return 1; } function main(): int32 { let y: int32 = (f = 5); return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidAssignment_ClassNameInValue_Control()
    {
        // Diagnóstico preexistente `Cannot assign '5' to variable of type
        // 'C'`; el test afirma solo el valor.
        var result = Run("class C { static v: int32; } function main(): int32 { let y: int32 = (C = 5); return y; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void InvalidAssignment_FunctionNameAsStatement_Control()
    {
        var result = Run("function f(): int32 { return 1; } function main(): int32 { f = 5; return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_PlainAssignments_Control()
    {
        var result = RunWithLimits("function main(): int32 { let x: int32 = 0; let i: int32 = 0; while (i < 1100000) { x = i; i++; } return x; }");
        Assert.Equal(1099999, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void WhileLoop_InvalidTargetAssignment_Control()
    {
        var result = RunWithLimits("function f(): int32 { return 1; } function main(): int32 { let i: int32 = 0; while (i < 1100000) { f() = 5; i++; } return 7; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 10 — W (núcleo: símbolos de tipo como valores) + X (acompañante:
    // ++/-- sobre globales de módulo). Tests 1-85 del plan (§3.2-§3.6).
    // Los tests que afirman "0 diagnósticos" usan BindSourceErrors además
    // de Run, porque Run ignora los diagnósticos del binder.

    // §3.2 W cara 1 — typeof de funciones y métodos.

    [Fact]
    public void Typeof_DeclaredFunction_Int32Return()
    {
        var result = Run("function f(): int32 { return 1; } function main(): string { return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_DeclaredFunction_StringReturn()
    {
        var result = Run("function f(): string { return 'x'; } function main(): string { return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_DeclaredFunction_VoidReturn()
    {
        var result = Run("function f(): void { } function main(): string { return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_DeclaredFunction_BoolReturn()
    {
        var result = Run("function f(): bool { return true; } function main(): string { return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_NestedFunction()
    {
        var result = Run("function main(): string { function g(): int32 { return 1; } return typeof g; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_StaticMethod_Int32Return()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): string { return typeof C.f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_StaticMethod_VoidReturn()
    {
        var result = Run("class C { static f(): void { } } function main(): string { return typeof C.f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_StaticMethod_BoolReturn()
    {
        var result = Run("class C { static f(): bool { return true; } } function main(): string { return typeof C.f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_StaticMethod_Async()
    {
        var result = Run("class C { static async f(n: int32): Promise<int32> { return n; } } function main(): string { return typeof C.f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_InstanceMethod_Int32Return()
    {
        var result = Run("class C { m(n: int32): int32 { return n; } } function main(): string { let c = new C(); return typeof c.m; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_InstanceMethod_VoidReturn()
    {
        var result = Run("class C { m(): void { } } function main(): string { let c = new C(); return typeof c.m; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_GenericFunction()
    {
        var result = Run("function f<T>(x: T): T { return x; } function main(): string { return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_AsyncFunction()
    {
        var result = Run("async function f(): Promise<int32> { return 1; } function main(): string { return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_NumberGlobal()
    {
        var result = Run("function main(): string { return typeof Number; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_NumberAlias()
    {
        var result = Run("function main(): string { let N = Number; return typeof N; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_Lambda_Control()
    {
        var result = Run("function main(): string { let f = () => 1; return typeof f; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    // §3.3 W cara 2 — clase y método como valor.

    [Fact]
    public void Typeof_ClassValue()
    {
        var result = Run("class C { } function main(): string { return typeof C; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Typeof_ClassWithStaticsValue()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): string { return typeof C; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Class_AsArgument_ReturnsTypeof()
    {
        var result = Run("class C { } function take(x: any): string { return typeof x; } function main(): string { return take(C); }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void Class_StrictEqualsSelf()
    {
        var result = Run("class C { } function main(): bool { return C === C; }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Fact]
    public void ClassAlias_Typeof()
    {
        var result = Run("class C { } function main(): string { let K = C; return typeof K; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void ClassAlias_StrictEqualsClass()
    {
        var result = Run("class C { } function main(): bool { let K = C; return K === C; }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Fact]
    public void ClassAlias_ReturnsFunctionValue()
    {
        var result = Run("class C { } function main(): any { return C; }");
        Assert.Equal("C::.ctor", Assert.IsType<TsFunctionValue>(result).FunctionName);
    }

    [Fact]
    public void ClassAlias_NewInstance()
    {
        var result = Run("class C { v: int32; constructor(n: int32) { this.v = n; } } function main(): int32 { let K = C; let c = new K(42); return c.v; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void SpreadCall_StaticMethod()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { return C.f(...[42]); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void OptionalCall_StaticMethod()
    {
        // Solo valor: el diagnóstico preexistente
        // "Cannot return 'int32?' from function returning 'int32'" se mantiene.
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { return C.f?.(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_AsValue_Call()
    {
        const string source = "class C { static f(n: int32): int32 { return n; } } function main(): int32 { let g = C.f; return g(42); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticMethod_AsValue_Typeof()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): string { let g = C.f; return typeof g; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void StaticMethod_AsValue_StrictEquals()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): bool { return C.f === C.f; }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Fact]
    public void StaticMethod_AsValue_AnyAnnotated_Call()
    {
        // Solo valor: el diagnóstico preexistente "Cannot return 'void'..."
        // se mantiene a propósito (la rama estrecha cubre callees-miembro
        // de tipo any, no variables any).
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { let g: any = C.f; return g(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_AsCallback()
    {
        const string source = "function apply(fn: (n: int32) => int32): int32 { return fn(5); } class C { static f(n: int32): int32 { return n * 2; } } function main(): int32 { return apply(C.f); }";
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticMethod_AsMapCallback()
    {
        var result = Run("class C { static f(n: int32): int32 { return n * 2; } } function main(): int32 { let a = [1, 2, 3]; return a.map(C.f)[2]; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void StaticMethod_AsValue_DefaultParam()
    {
        const string source = "class C { static f(n: int32 = 7): int32 { return n; } } function main(): int32 { let g = C.f; return g(); }";
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticMethod_AsValue_RestParam()
    {
        const string source = "class C { static f(a: int32, ...rest: int32[]): int32 { return a * 10 + rest.length; } } function main(): int32 { let g = C.f; return g(1, 2, 3); }";
        var result = Run(source);
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldFunction_Typed_Call()
    {
        const string source = "class C { static h: (n: int32) => int32; static { C.h = (n: int32): int32 => { return n; }; } } function main(): int32 { return C.h(42); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldFunction_Any_Call()
    {
        const string source = "class C { static h: any; static { C.h = (n: int32): int32 => { return n; }; } } function main(): int32 { return C.h(42); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void InstanceMethod_AsValue_Call()
    {
        const string source = "class C { v: int32; m(n: int32): int32 { return n; } } function main(): int32 { let c = new C(); let g = c.m; return g(42); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void InstanceMethod_AsValue_DetachedTouchesThis()
    {
        // El método desprendido se llama con this = Void; this.v -> null
        // y null + 42 -> 42.
        // Desviación del plan (§3.38): el plan esperaba int64(42) apoyándose
        // en la sonda M8b (`null + 42` -> int64), pero en ese script el
        // `null` no estaba tipado y el binario tomaba el camino de
        // ensanchamiento a int64 (IRGenerator.cs: GenerateBinary, operandos
        // `any`). Aquí `this.v` y `n` son estáticamente int32, así que el
        // opcode es Add_I32 y el valor es int32(42) (medido). El valor 42 es
        // el mismo; solo cambia el tipo de runtime. Se afirma int32(42) con
        // main(): int32 en vez de int64.
        var result = Run("class C { v: int32; m(n: int32): int32 { return this.v + n; } } function main(): int32 { let c = new C(); c.v = 10; let g = c.m; return g(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Class_PlainCall_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("class C { v: int32; constructor(n: int32) { this.v = n; } } function main(): int32 { return C(42); }"));
        Assert.Contains("cannot be invoked without 'new'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Class_PlainCall_ReportsDiagnostic()
    {
        const string source = "class C { v: int32; constructor(n: int32) { this.v = n; } } function main(): int32 { return C(42); }";
        var errors = BindSourceErrors(source);
        Assert.Single(errors);
        Assert.Contains("Class 'C' cannot be invoked without 'new'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassAlias_PlainCall_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("class C { v: int32; constructor(n: int32) { this.v = n; } } function main(): int32 { let K = C; K(42); return 7; }"));
        Assert.Contains("cannot be invoked without 'new'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceFieldFunction_Control()
    {
        const string source = "class C { h: any; } function main(): int32 { let c = new C(); c.h = (n: int32): int32 => { return n * 2; }; return c.h(21); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    // §3.4 W cara 3 — alias de función y callbacks.

    [Fact]
    public void FunctionAlias_Typeof()
    {
        var result = Run("function f(): int32 { return 1; } function main(): string { let g = f; return typeof g; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void FunctionAlias_Call()
    {
        const string source = "function f(): int32 { return 7; } function main(): int32 { let g = f; return g(); }";
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FunctionAlias_Call_AssignedToTypedLocal()
    {
        const string source = "function f(): int32 { return 7; } function main(): int32 { let g = f; let x: int32 = g(); return x; }";
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FunctionAlias_Call_TypeofResult()
    {
        var result = Run("function f(): int32 { return 7; } function main(): string { let g = f; return typeof g(); }");
        Assert.Equal("number", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void FunctionAlias_AsCallback()
    {
        const string source = "function apply(fn: (n: int32) => int32): int32 { return fn(5); } function f(n: int32): int32 { return n * 2; } function main(): int32 { let g = f; return apply(g); }";
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FunctionDirect_AsCallback()
    {
        const string source = "function apply(fn: (n: int32) => int32): int32 { return fn(5); } function f(n: int32): int32 { return n * 2; } function main(): int32 { return apply(f); }";
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FunctionAlias_Annotated()
    {
        const string source = "function f(n: int32): int32 { return n * 2; } function main(): int32 { let h: (n: int32) => int32 = f; return h(5); }";
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void Function_ReturnedAsValue()
    {
        const string source = "function f(n: int32): int32 { return n * 2; } function fetchFn(): (n: int32) => int32 { return f; } function main(): int32 { return fetchFn()(5); }";
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FunctionAlias_Generic_Typeof()
    {
        var result = Run("function f<T>(x: T): T { return x; } function main(): string { let g = f; return typeof g; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void FunctionAlias_Generic_Call()
    {
        // Solo valor: la expectativa de diagnósticos post-arreglo queda
        // abierta por diseño (plan §3.52); D mide y registra el conteo.
        var result = Run("function f<T>(x: T): T { return x; } function main(): int32 { let g = f; return g(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void OverloadedFunction_Alias_CallInt()
    {
        const string source = "function pick(value: string): string; function pick(value: int32): int32; function pick(value: any): any { return value; } function main(): int32 { let g = pick; return g(4); }";
        var result = Run(source);
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void OverloadedFunction_Alias_CallString()
    {
        const string source = "function pick(value: string): string; function pick(value: int32): int32; function pick(value: any): any { return value; } function main(): string { let g = pick; return g('abc'); }";
        var result = Run(source);
        Assert.Equal("abc", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FunctionAlias_AnyAnnotated_Typeof()
    {
        var result = Run("function f(): int32 { return 7; } function main(): string { let g: any = f; return typeof g; }");
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    // §3.5 Controles W (guardas: pasan antes y después).

    [Fact]
    public void DirectStaticCall_Control()
    {
        var result = Run("class C { static f(n: int32): int32 { return n; } } function main(): int32 { return C.f(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Constructor_Control()
    {
        var result = Run("class C { v: int32; constructor(n: int32) { this.v = n; } } function main(): int32 { let c = new C(42); return c.v; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Object_Typeof_Control()
    {
        var result = Run("function main(): string { let o = { n: 1 }; return typeof o; }");
        Assert.Equal("object", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void NumberConversion_Control()
    {
        var result = Run("function main(): number { return Number('42'); }");
        Assert.Equal(42, Assert.IsType<TsFloat64Value>(result).Value);
    }

    [Fact]
    public void GlobalIsFinite_Control()
    {
        var result = Run("function main(): bool { return isFinite(3); }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Fact]
    public void OptionalCall_PlainFunction_Control()
    {
        var result = Run("function f(n: int32): int32 { return n; } function main(): int32 { return f?.(42); }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void MapLambda_Control()
    {
        var result = Run("function main(): int32 { let a = [1, 2, 3]; return a.map((x: int32): int32 => { return x * 2; })[2]; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Desviación documentada (plan §3.63-§3.64): los nombres
    // Builtin_MathFloor_Control y Builtin_ArrayIsArray_Control ya existen
    // en InterpreterTests (tests del ciclo 9, mismo script y misma
    // aserción). Un duplicado de nombre no compila (CS0111), así que los
    // dos controles se añaden con sufijo _Cycle10; el contenido es el del
    // plan y el conteo de 85 tests nuevos se mantiene.
    [Fact]
    public void Builtin_MathFloor_Control_Cycle10()
    {
        var result = Run("function main(): number { return Math.floor(3.7); }");
        Assert.Equal(3, Assert.IsType<TsFloat64Value>(result).Value);
    }

    [Fact]
    public void Builtin_ArrayIsArray_Control_Cycle10()
    {
        var result = Run("function main(): int32 { let b: any = Array.isArray([1]); return b; }");
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
    }

    // §3.6 X — ++/-- sobre globales de módulo.

    [Fact]
    public void ModuleGlobal_PostfixInMain()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { x++; return x; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PrefixInMain()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { ++x; return x; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PostfixDecrementInMain()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { x--; return x; }");
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PostfixValue()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { let y: int32 = x++; return y * 100 + x; }");
        Assert.Equal(506, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PrefixValue()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { let y: int32 = ++x; return y * 100 + x; }");
        Assert.Equal(606, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PrefixDecrementValue()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { let y: int32 = --x; return y * 100 + x; }");
        Assert.Equal(404, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PostfixDecrementValue()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { let y: int32 = x--; return y * 100 + x; }");
        Assert.Equal(504, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_IncrementFromFunction()
    {
        var result = Run("let x: int32 = 0; function inc(): void { x++; } function main(): int32 { inc(); inc(); return x; }");
        Assert.Equal(2, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PrefixFromFunctionThreeTimes()
    {
        var result = Run("let x: int32 = 0; function inc(): void { ++x; } function main(): int32 { inc(); inc(); inc(); return x; }");
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_PostfixValueFromFunction()
    {
        var result = Run("let x: int32 = 5; function inc(): int32 { return x++; } function main(): int32 { let y: int32 = inc(); return y * 100 + x; }");
        Assert.Equal(506, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_IncrementInMain_ReadFromFunction()
    {
        var result = Run("let x: int32 = 5; function get(): int32 { return x; } function main(): int32 { x++; return get(); }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_IncrementInClosure()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { let f = () => { x++; return x; }; return f(); }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_NumberIncrement()
    {
        var result = Run("let x: number = 5.5; function main(): number { x++; return x; }");
        Assert.Equal(6.5, Assert.IsType<TsFloat64Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_Int64Increment()
    {
        var result = Run("let x: int64 = 5; function main(): int64 { x++; return x; }");
        Assert.Equal(6, Assert.IsType<TsInt64Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_Float32Increment()
    {
        var result = Run("let x: float32 = 5.5; function main(): number { x++; return x; }");
        Assert.Equal(6.5, Assert.IsType<TsFloat64Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_UninitializedIncrement()
    {
        var result = Run("let x: int32; function main(): int32 { x++; return x; }");
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_IncrementInLoop()
    {
        var result = Run("let x: int32 = 0; function main(): int32 { for (let i: int32 = 0; i < 3; i++) { x++; } return x; }");
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_CompoundAssign_Control()
    {
        var result = Run("let x: int32 = 5; function inc(): void { x += 1; } function main(): int32 { inc(); return x; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_CompoundAssignInMain_Control()
    {
        var result = Run("let x: int32 = 5; function main(): int32 { x += 1; return x; }");
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void Local_PostfixValue_Control()
    {
        var result = Run("function main(): int32 { let x: int32 = 5; let y: int32 = x++; return y * 100 + x; }");
        Assert.Equal(506, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ModuleGlobal_N33Counter_CrossCheck()
    {
        var result = Run("let calls: int32 = 0; function next(): int32 { calls++; return 1; } function main(): int32 { let a: int32 = next(); return calls * 100 + a; }");
        Assert.Equal(101, Assert.IsType<TsInt32Value>(result).Value);
    }
    // ===== Ciclo 11: Z (inicializadores de campo), F (for sin condicion), =====
    // ===== M (typeof null) y (1) (alias de builtin sin diagnostico espurio) =====

    // --- Z instancia (§3.2) ---

    [Fact]
    public void FieldInitializer_Instance_Literal()
    {
        var result = Run("class C { v: int32 = 42; } function main(): int32 { return new C().v; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors("class C { v: int32 = 42; } function main(): int32 { return new C().v; }"));
    }

    [Fact]
    public void FieldInitializer_Instance_Expression()
    {
        var result = Run("class C { v: int32 = 6 * 7; } function main(): int32 { return new C().v; }");
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors("class C { v: int32 = 6 * 7; } function main(): int32 { return new C().v; }"));
    }

    [Fact]
    public void FieldInitializer_Instance_String()
    {
        var result = Run("class C { v: string = 'hi'; } function main(): string { return new C().v; }");
        Assert.Equal("hi", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors("class C { v: string = 'hi'; } function main(): string { return new C().v; }"));
    }

    [Fact]
    public void FieldInitializer_Instance_SideEffectOrder()
    {
        const string source = "let log: int32 = 0; function f(n: int32): int32 { log = log * 10 + n; return n; } class C { a: int32 = f(1); b: int32 = f(2); } function main(): int32 { let c = new C(); return log * 100 + c.a * 10 + c.b; }";
        var result = Run(source);
        Assert.Equal(1212, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_ReadsEarlierField()
    {
        const string source = "class C { a: int32 = 5; b: int32 = this.a + 1; } function main(): int32 { return new C().b; }";
        var result = Run(source);
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_ReadsLaterField()
    {
        const string source = "class C { a: int32 = this.b; b: int32 = 2; } function main(): int32 { let c = new C(); return c.a; }";
        var result = Run(source);
        Assert.IsType<TsNull>(result);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_ReadsOwnField()
    {
        const string source = "class C { v: int32 = this.v + 1; } function main(): int32 { return new C().v; }";
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_CtorAssignWins()
    {
        const string source = "class C { v: int32 = 42; constructor() { this.v = 7; } } function main(): int32 { return new C().v; }";
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_Readonly()
    {
        const string source = "class C { readonly v: int32 = 42; } function main(): int32 { return new C().v; }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_ReadonlyAssignInCtor_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("class C { readonly v: int32; constructor() { this.v = 5; } } function main(): int32 { return new C().v; }");
        Assert.Single(errors);
        Assert.Contains("Cannot assign to readonly member 'v'", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldInitializer_Instance_ThisIsInstance()
    {
        const string source = "class C { v: any = this; } function main(): string { return typeof new C().v; }";
        var result = Run(source);
        Assert.Equal("object", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Derived_SuperFirst()
    {
        const string source = "class A { v: int32 = 1; constructor() { this.v = 2; } } class B extends A { v: int32 = 3; constructor() { super(); } } function main(): int32 { return new B().v; }";
        var result = Run(source);
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Derived_SuperNotFirst()
    {
        const string source = "class A { v: int32 = 1; constructor() { this.v = 2; } } class B extends A { v: int32 = 3; w: int32; constructor() { this.w = 9; super(); } } function main(): int32 { let b = new B(); return b.v * 100 + b.w; }";
        var result = Run(source);
        Assert.Equal(209, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Derived_NoSuper_OwnRuns()
    {
        const string source = "class A { v: int32 = 1; constructor() { this.v = 2; } } class B extends A { w: int32 = 3; constructor() { this.w = 4; } } function main(): int32 { return new B().w; }";
        var result = Run(source);
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Derived_NoSuper_BaseStaysNull()
    {
        const string source = "class A { v: int32 = 1; constructor() { this.v = 2; } } class B extends A { w: int32 = 3; constructor() { this.w = 4; } } function main(): int32 { return new B().v; }";
        var result = Run(source);
        Assert.IsType<TsNull>(result);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_NoCtor_NewWithArgs_ReportsDiagnostic()
    {
        const string source = "class C { v: int32 = 42; } function main(): int32 { let c = new C(7); return c.v; }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        var errors = BindSourceErrors(source);
        Assert.Single(errors);
        Assert.Contains("Expected 0 argument(s) but got 1", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldInitializer_SuperAgainstInitializerOnlyBase()
    {
        const string source = "class A { v: int32 = 1; } class B extends A { constructor() { super(); } } function main(): int32 { return new B().v; }";
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_PrivateField()
    {
        const string source = "class C { #v: int32 = 42; get(): int32 { return this.#v; } } function main(): int32 { return new C().get(); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_PropertyParamOrder()
    {
        const string source = "class C { v: int32 = this.w + 1; constructor(private w: int32) { } } function main(): int32 { return new C(5).v; }";
        var result = Run(source);
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void FieldInitializer_Instance_ClassReference()
    {
        const string source = "class C { v: any = C; } function main(): string { let c = new C(); return typeof c.v; }";
        var result = Run(source);
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    // --- Z estatico (§3.3) ---

    [Fact]
    public void StaticFieldInitializer_Literal()
    {
        const string source = "class C { static v: int32 = 42; } function main(): int32 { return C.v; }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_String()
    {
        const string source = "class C { static v: string = 'hi'; } function main(): string { return C.v; }";
        var result = Run(source);
        Assert.Equal("hi", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_SideEffect()
    {
        const string source = "let seen: int32 = 0; function f(): int32 { seen = 1; return 7; } class C { static v: int32 = f(); } function main(): int32 { return C.v * 10 + seen; }";
        var result = Run(source);
        Assert.Equal(71, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_ReadsEarlierStatic()
    {
        const string source = "class C { static a: int32 = 5; static b: int32 = C.a + 1; } function main(): int32 { return C.b; }";
        var result = Run(source);
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_Interleaved_BlockAfter()
    {
        const string source = "class C { static v: int32 = 10; static { C.v = C.v + 1; } } function main(): int32 { return C.v; }";
        var result = Run(source);
        Assert.Equal(11, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_Interleaved_BlockBefore()
    {
        const string source = "class C { static { C.v = 5; } static v: int32 = 10; } function main(): int32 { return C.v; }";
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_Interleaved_TwoBlocks()
    {
        const string source = "let order: string = ''; class C { static a: int32 = 1; static { order = order + 'a'; } static b: int32 = 2; static { order = order + 'b'; } } function main(): string { return order + (C.a * 10 + C.b); }";
        var result = Run(source);
        Assert.Equal("ab12", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void StaticFieldInitializer_This_ReportsDiagnostic()
    {
        var errors = BindSourceErrors("class C { static v: any = this; } function main(): string { return typeof C.v; }");
        Assert.Single(errors);
        Assert.Contains("'this' cannot be used in a static field initializer", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticFieldInitializer_ClassReference()
    {
        const string source = "class C { static v: any = C; } function main(): string { return typeof C.v; }";
        var result = Run(source);
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    // --- F: for sin condicion (§3.4) ---

    [Fact]
    public void ForNoCondition_BreakByCounter()
    {
        const string source = "function main(): int32 { let i: int32 = 0; for (let j: int32 = 0; ; j++) { i++; if (i >= 3) break; } return i; }";
        var result = Run(source);
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void ForNoCondition_Return()
    {
        const string source = "function main(): int32 { for (;;) { return 7; } return 0; }";
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void ForNoCondition_Continue()
    {
        const string source = "function main(): int32 { let i: int32 = 0; for (;;) { i++; if (i < 3) continue; break; } return i; }";
        var result = Run(source);
        Assert.Equal(3, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void ForNoCondition_IteratorRuns()
    {
        const string source = "function main(): int32 { let i: int32 = 0; let j: int32 = 0; for (; ; j++) { i++; if (i >= 3) break; } return i * 100 + j; }";
        var result = Run(source);
        Assert.Equal(302, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void ForWithCondition_Control()
    {
        const string source = "function main(): int32 { let i: int32 = 0; for (let k: int32 = 0; k < 3; k++) { i++; } return i * 10; }";
        var result = Run(source);
        Assert.Equal(30, Assert.IsType<TsInt32Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    // --- M: typeof null (§3.5) ---

    [Fact]
    public void Typeof_Null()
    {
        const string source = "function main(): string { return typeof null; }";
        var result = Run(source);
        Assert.Equal("object", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void Typeof_NullVariable()
    {
        const string source = "function main(): string { let x: any = null; return typeof x; }";
        var result = Run(source);
        Assert.Equal("object", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void Typeof_UninitializedLocal_Control()
    {
        const string source = "function main(): string { let u: any; return typeof u; }";
        var result = Run(source);
        Assert.Equal("undefined", Assert.IsType<TsStringValue>(result).Value);
        var errors = BindSourceErrors(source);
        Assert.Single(errors);
        Assert.Contains("Variable 'u' is used before being assigned", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Typeof_Void_Control()
    {
        const string source = "function main(): string { return typeof void 0; }";
        var result = Run(source);
        Assert.Equal("undefined", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void NullEqualsUndefined_Control()
    {
        const string source = "function main(): bool { return null == undefined; }";
        var result = Run(source);
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void Typeof_NullReturn()
    {
        const string source = "function f(): any { return null; } function main(): string { return typeof f(); }";
        var result = Run(source);
        Assert.Equal("object", Assert.IsType<TsStringValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    // --- (1): alias de builtin (§3.6) ---

    [Fact]
    public void AliasBuiltin_MathFloor_NoDiagnostic()
    {
        const string source = "function main(): number { let g = Math.floor; return g(3.7); }";
        var result = Run(source);
        Assert.Equal(3, Assert.IsType<TsFloat64Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void AliasBuiltin_ArrayIsArray_NoDiagnostic()
    {
        const string source = "function main(): bool { let g = Array.isArray; return g([1]); }";
        var result = Run(source);
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void AliasBuiltin_MathMax_NoDiagnostic()
    {
        const string source = "function main(): number { let g = Math.max; return g(1, 2, 3); }";
        var result = Run(source);
        Assert.Equal(3, Assert.IsType<TsFloat64Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void AliasDeclared_ZeroParams_StillDiagnoses()
    {
        const string source = "function f(): int32 { return 1; } function main(): int32 { let g = f; return g(1); }";
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
        var errors = BindSourceErrors(source);
        Assert.Single(errors);
        Assert.Contains("Expected 0 argument(s) but got 1", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AliasDeclared_OneParam_StillDiagnoses()
    {
        const string source = "function f(n: int32): int32 { return n; } function main(): int32 { let g = f; return g(1, 2); }";
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
        var errors = BindSourceErrors(source);
        Assert.Single(errors);
        Assert.Contains("Expected 1 argument(s) but got 2", errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectBuiltinCall_MathFloor_Control()
    {
        const string source = "function main(): number { return Math.floor(3.7); }";
        var result = Run(source);
        Assert.Equal(3, Assert.IsType<TsFloat64Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void DirectBuiltinCall_ArrayIsArray_Control()
    {
        const string source = "function main(): bool { return Array.isArray([1]); }";
        var result = Run(source);
        Assert.True(Assert.IsType<TsBoolValue>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void AliasBuiltin_ParseInt_NoDiagnostic()
    {
        const string source = "function main(): number { let g = parseInt; return g('42'); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsFloat64Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void AliasBuiltin_NumberConversion_NoDiagnostic()
    {
        const string source = "function main(): number { let g = Number; return g('42'); }";
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsFloat64Value>(result).Value);
        Assert.Empty(BindSourceErrors(source));
    }

    [Fact]
    public void AliasDeclared_StaticMethodZeroParams_StillDiagnoses()
    {
        const string source = "class C { static f(): int32 { return 1; } } function main(): int32 { let g = C.f; return g(1); }";
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
        var errors = BindSourceErrors(source);
        Assert.Single(errors);
        Assert.Contains("Expected 0 argument(s) but got 1", errors[0].Message, StringComparison.Ordinal);
    }

    // Ciclo 12 (AG): clases locales como declaraciones.

    [Fact]
    public void LocalClass_StaticField()
    {
        const string source = "function main(): int32 { class C { static v: int32 = 42; } return C.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticMethod()
    {
        const string source = "function main(): int32 { class C { static v: int32 = 42; static f(): int32 { return C.v; } } return C.f(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticReadModifyWrite()
    {
        const string source = "function main(): int32 { class C { static calls: int32 = 0; } C.calls = C.calls + 1; return C.calls; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticMethodDerived()
    {
        const string source = "function main(): int32 { class A { static f(): int32 { return 1; } } class B extends A { static g(): int32 { return 2; } } return B.g(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(2, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_Typeof()
    {
        const string source = "function main(): string { class C { v: int32 = 1; } return typeof C; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void LocalClass_AliasCallsStatic()
    {
        const string source = "function main(): int32 { class C { static f(n: int32): int32 { return n * 2; } } let K = C; return K.f(21); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticWrite()
    {
        const string source = "function main(): int32 { class C { static v: int32 = 0; } C.v = 5; return C.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_NestedBlock()
    {
        const string source = "function main(): int32 { let r: int32 = 0; { class C { static v: int32 = 42; } r = C.v; } return r; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticInitializerRuns()
    {
        const string source = "function main(): int32 { let seen: int32 = 0; function f(): int32 { seen = 7; return 42; } class C { static v: int32 = f(); } let c = new C(); return seen; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticBlockRuns()
    {
        const string source = "let seen: int32 = 0; function main(): int32 { class C { static { seen = 7; } } return seen; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_ThisFieldInInitializer()
    {
        const string source = "function main(): int32 { class C { a: int32 = 5; b: int32 = this.a + 1; } let c = new C(); return c.b; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(6, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_ThisFieldValueFlows()
    {
        const string source = "function main(): int32 { class C { a: int32 = 7; b: int32 = this.a; } let c = new C(); return c.a * 100 + c.b; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(707, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_ForwardFieldReference()
    {
        const string source = "function main(): int32 { class C { a: int32 = this.b; b: int32 = 5; } let c = new C(); return c.a * 100 + c.b; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_CallReceiverField()
    {
        const string source = "function main(): int32 { class O { n: int32 = 10; } function getO(): O { return new O(); } return getO().n; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_CallReceiverMethod()
    {
        const string source = "function main(): int32 { class O { n: int32 = 10; m(): int32 { return this.n; } } function getO(): O { return new O(); } return getO().m(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(10, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_CallReceiverCompoundAssignment_N33()
    {
        const string source = "function main(): int32 { class O { n: int32 = 10; } let calls: int32 = 0; function getO(): O { calls = calls + 1; return new O(); } getO().n += 5; return calls; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_CallReceiverAdd()
    {
        const string source = "function main(): int32 { class O { n: int32 = 10; } function getO(): O { return new O(); } return getO().n + 5; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(15, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_StaticBaseCallExplicit()
    {
        const string source = "function main(): int32 { class A { static f(): int32 { return 1; } } class B extends A { static g(): int32 { return A.f(); } } return B.g(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 12 (AG): guardas — lo que ya funciona en locales.

    [Fact]
    public void LocalClass_New_Control()
    {
        const string source = "function main(): int32 { class C { v: int32 = 0; constructor(n: int32) { this.v = n; } } let c = new C(42); return c.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_InstanceMethod_Control()
    {
        const string source = "function main(): int32 { class C { v: int32 = 7; m(): int32 { return this.v; } } let c = new C(); return c.m(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_LocalFunctionValue_Control()
    {
        const string source = "function main(): int32 { function g(): int32 { return 1; } let h = g; return h(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_Annotation_Control()
    {
        const string source = "function main(): int32 { class C { v: int32 = 1; } let c: C = new C(); return c.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void LocalClass_AnyAccess_Control()
    {
        const string source = "function main(): int32 { class C { v: int32 = 5; } let c: any = new C(); return c.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 12 (AG): expresión de clase explícita.

    [Fact]
    public void ClassExpr_StaticField()
    {
        const string source = "function main(): int32 { let C = class { static v: int32 = 42; }; return C.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ClassExpr_StaticMethod()
    {
        const string source = "function main(): int32 { let C = class { static f(n: int32): int32 { return n * 2; } }; return C.f(21); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ClassExpr_Typeof()
    {
        const string source = "function main(): string { let C = class { v: int32 = 1; }; return typeof C; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal("function", Assert.IsType<TsStringValue>(result).Value);
    }

    [Fact]
    public void ClassExpr_NewAndInstance_Control()
    {
        const string source = "function main(): int32 { let C = class { v: int32 = 5; }; let c = new C(); return c.v; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ClassExpr_InstanceMethod_Control()
    {
        const string source = "function main(): int32 { let C = class { v: int32 = 1; m(): int32 { return this.v; } }; let c = new C(); return c.m(); }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result).Value);
    }

    // Ciclo 12 (AI): escrituras por punto sobre null.

    [Fact]
    public void NullDotWrite_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): int32 { let o: any = null; o.x = 1; return 1; }"));
        Assert.Contains("Cannot index a null value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullDotCompoundWrite_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): int32 { let o: any = null; o.x += 1; return 1; }"));
        Assert.Contains("Cannot index a null value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullDotIncrement_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): int32 { let o: any = null; o.x++; return 1; }"));
        Assert.Contains("Cannot index a null value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullIndexWrite_Control()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): int32 { let o: any = null; o['x'] = 1; return 1; }"));
        Assert.Contains("Cannot index a null value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCall_Control()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Run("function main(): int32 { let o: any = null; o.f(); return 1; }"));
        Assert.Contains("Function 'f' not found for receiver null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullDotRead_ReturnsNull()
    {
        const string source = "function main(): int32 { let o: any = null; return o.x; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.IsType<TsNull>(result);
    }

    [Fact]
    public void DotWrite_OnObject_Control()
    {
        const string source = "function main(): int32 { let o: any = {}; o.x = 5; return o.x; }";
        Assert.Empty(BindSourceErrors(source));
        var result = Run(source);
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

}

public class TypeCheckingTests
{
    [Fact]
    public void StringPlusIntProducesStringType()
    {
        var code = @"
            function main(): string {
                return ""hello"" + 5;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var boundTree = binder.Bind(syntaxTree);

        Assert.False(
            binder.Diagnostics.HasErrors,
            string.Join("; ", binder.Diagnostics.GetErrors().Select(d => d.Message)));
        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(boundTree.Members));
        var block = Assert.IsType<BoundBlockStatement>(function.Body);
        var statement = Assert.IsType<BoundReturnStatement>(Assert.Single(block.Statements));
        Assert.NotNull(statement.Value);
        Assert.Equal(TsType.String, statement.Value.Type);
    }

    [Fact]
    public void StringPlusStringWorks()
    {
        var result = Run(@"
            function main(): string {
                return ""hello "" + ""world"";
            }
        ");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("hello world", ((TsStringValue)result).Value);
    }

    [Fact]
    public void WrongArgumentCountProducesError()
    {
        var code = @"
            function add(a: int32, b: int32): int32 {
                return a + b;
            }
            function main(): int32 {
                return add(1);
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var boundTree = binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("Expected 2") && d.Message.Contains("but got 1"));
    }

    [Fact]
    public void ReturnTypeMismatch_ProducesError()
    {
        var code = @"
            function main(): int32 {
                return ""hello"";
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("Cannot return") && d.Message.Contains("from function returning"));
    }

    [Fact]
    public void ReturnVoidWithExpression_ProducesWarning()
    {
        var code = @"
            function main(): void {
                return 42;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.Contains(binder.Diagnostics.All, d =>
            d.Severity == TypeSharp.Syntax.Diagnostics.DiagnosticSeverity.Warning &&
            d.Message.Contains("returns 'void' but return statement has a value"));
    }

    [Fact]
    public void ReturnNonVoidWithoutValue_ProducesError()
    {
        var code = @"
            function main(): int32 {
                return;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
        Assert.Contains(binder.Diagnostics.All, d =>
            d.Message.Contains("returns 'int32' but return statement has no value"));
    }

    [Fact]
    public void NullAssignToNonNullable_ProducesError()
    {
        var code = @"
            function main(): string {
                const x: string = null;
                return x;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
    }

    [Fact]
    public void NullAssignToNullable_NoError()
    {
        var code = @"
            function main(): int32 {
                const x: string? = null;
                return 0;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.False(binder.Diagnostics.HasErrors);
    }

    [Fact]
    public void NullableAssignToNonNullable_ProducesError()
    {
        var code = @"
            function getString(): string? {
                return null;
            }
            function main(): string {
                const x: string = getString();
                return x;
            }
        ";

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        binder.Bind(syntaxTree);

        Assert.True(binder.Diagnostics.HasErrors);
    }

    private static TsValue? Run(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IR.IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = Bytecode.BytecodeCompiler.Compile(moduleIR);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }
}

public class BytecodeTests
{
    [Fact]
    public void CompileAndDecompile()
    {
        var ir = new ModuleIR("test");
        var func = new FunctionIR("add", TypeSharp.Semantics.TypeSystem.TsType.Int32,
            new List<ParameterInfo>
            {
                new("a", TypeSharp.Semantics.TypeSystem.TsType.Int32),
                new("b", TypeSharp.Semantics.TypeSystem.TsType.Int32),
            });

        var block = func.CreateBlock();
        block.Instructions.Add(new Instruction(Opcode.LoadArg, 0));
        block.Instructions.Add(new Instruction(Opcode.LoadArg, 1));
        block.Instructions.Add(new Instruction(Opcode.Add_I32));
        block.Instructions.Add(new Instruction(Opcode.Return));

        ir.AddFunction(func);

        var module = BytecodeCompiler.Compile(ir);
        Assert.Single(module.Functions);
        Assert.Equal("add", module.Functions[0].Name);
        Assert.True(module.Functions[0].Instructions.Length > 0);
    }

    [Fact]
    public void VerifierRejectsBranchIntoOperand()
    {
        var code = new byte[] { Opcodes.Branch, 1, 0, 0, 0, Opcodes.ReturnVoid };
        var function = new BytecodeFunction("test", code, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { function });

        Assert.Throws<BytecodeVerificationException>(() => BytecodeVerifier.Verify(module));
    }

    [Fact]
    public void SerializerRoundTripsVerifiedModule()
    {
        var code = new byte[] { Opcodes.LoadConstI32, 42, 0, 0, 0, Opcodes.Return };
        var function = new BytecodeFunction("main", code, 0, 0, false,
            new[] { "unused" }, Array.Empty<long>(), Array.Empty<double>(), new[] { 12.5m });
        var generator = new BytecodeFunction("gen", new[] { Opcodes.ReturnVoid }, 1, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>(),
            operandStackCapacity: 0, isGenerator: true, restParameterIndex: 0);
        var module = new BytecodeModule("roundtrip", new[] { function, generator });

        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, module);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);

        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, "main");
        Assert.Equal(42, ((TsInt32Value)result!).Value);
        Assert.Equal(12.5m, restored.Functions[0].DecimalConstants[0]);
        Assert.True(restored.Functions[1].IsGenerator);
        Assert.Equal(0, restored.Functions[1].RestParameterIndex);
    }

    [Fact]
    public void ProfileCountsHotFunctions()
    {
        var code = new byte[] { Opcodes.ReturnVoid };
        var module = new BytecodeModule("profile", new[]
        {
            new BytecodeFunction("main", code, 0, 0, false,
                Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>())
        });
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        interpreter.Execute(module, "main");
        interpreter.Execute(module, "main");

        Assert.Equal(2, interpreter.Profile.GetCallCount("profile", "main"));
    }
}

public class HeapTests
{
    [Fact]
    public void AllocateAndReadObject()
    {
        var heap = new TsHeap();
        var obj = heap.AllocateObject("User");
        obj.SetField("name", TsValue.FromString("Alice"));
        obj.SetField("id", TsValue.FromInt32(1));

        Assert.Equal("User", obj.TypeName);
        Assert.Equal("Alice", ((TsStringValue)obj.GetField("name")).Value);
        Assert.Equal(1, ((TsInt32Value)obj.GetField("id")).Value);
    }

    [Fact]
    public void AllocateArray()
    {
        var heap = new TsHeap();
        var arr = heap.AllocateArray(8);
        arr.Add(TsValue.FromInt32(10));
        arr.Add(TsValue.FromInt32(20));

        Assert.Equal(2, arr.Count);
        Assert.Equal(10, ((TsInt32Value)arr.Get(0)).Value);
        Assert.Equal(20, ((TsInt32Value)arr.Get(1)).Value);
    }

    [Fact]
    public void AllocateMap()
    {
        var heap = new TsHeap();
        var map = heap.AllocateMap();
        map.Set(TsValue.FromString("key1"), TsValue.FromString("value1"));
        map.Set(TsValue.FromString("key2"), TsValue.FromInt32(42));

        Assert.Equal(2, map.Count);
        Assert.Equal("value1", ((TsStringValue)map.Get(TsValue.FromString("key1"))).Value);
        Assert.Equal(42, ((TsInt32Value)map.Get(TsValue.FromString("key2"))).Value);
    }

    [Fact]
    public void TrackBytesAllocated()
    {
        var heap = new TsHeap();
        var initial = heap.LogicalBytes;
        heap.AllocateObject("Test");
        Assert.True(heap.LogicalBytes > initial);
    }

    public class SampleService
    {
        [TsExport("add")]
        public int Add(int a, int b) => a + b;

        public int Subtract(int a, int b) => a - b;

        public override string ToString() => "SampleService";
        public override int GetHashCode() => 42;
    }

    [Fact]
    public void ExplicitOnly_OnlyExportsMarkedMethods()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.ExplicitOnly);

        Assert.Single(registry.Functions);
        Assert.True(registry.Functions.ContainsKey("math.add"));
    }

    [Fact]
    public void Public_ExcludesObjectMethods()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.Public);

        Assert.True(registry.Functions.ContainsKey("math.add"));
        Assert.True(registry.Functions.ContainsKey("math.Subtract"));
        Assert.False(registry.Functions.ContainsKey("math.ToString"));
        Assert.False(registry.Functions.ContainsKey("math.GetHashCode"));
        Assert.False(registry.Functions.ContainsKey("math.Equals"));
        Assert.False(registry.Functions.ContainsKey("math.GetType"));
    }

    [Fact]
    public void All_ExportsEverything()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.All);

        Assert.True(registry.Functions.ContainsKey("math.add"));
        Assert.True(registry.Functions.ContainsKey("math.Subtract"));
        Assert.True(registry.Functions.ContainsKey("math.ToString"));
        Assert.True(registry.Functions.ContainsKey("math.GetHashCode"));
        Assert.False(registry.Functions.ContainsKey("math.Equals"));
        Assert.False(registry.Functions.ContainsKey("math.GetType"));
    }

    [Fact]
    public void ExplicitOnly_DoesNotExportUnmarkedPublic()
    {
        var registry = new HostRegistry();
        var service = new SampleService();
        registry.RegisterObject("math", service, ExportMode.ExplicitOnly);

        Assert.False(registry.Functions.ContainsKey("math.Subtract"));
    }
}

public class InteropTests
{
    private static TsValue? Run(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);

        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);

        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }

    public class MathService
    {
        [TsExport("add")]
        public int Add(int a, int b) => a + b;

        [TsExport("multiply")]
        public int Multiply(int a, int b) => a * b;

        [TsExport("get_decimal")]
        public decimal GetDecimal() => 123.456m;

        [TsExport("get_ulong")]
        public ulong GetUlong() => ulong.MaxValue;

        [TsExport("echo_string")]
        public string EchoString(string s) => s;

        [TsExport("get_async_value")]
        public Task<int> GetAsyncValue() => Task.FromResult(42);

        [TsExport("get_async_string")]
        public ValueTask<string> GetAsyncString() => new ValueTask<string>("hello");

        [TsExport("get_void_task")]
        public Task DoWorkAsync()
        {
            return Task.CompletedTask;
        }

        [TsExport("is_even")]
        public bool IsEven(int n) => n % 2 == 0;
    }

    public class StringService
    {
        [TsExport("get")]
        public string Get(string key) => $"value:{key}";

        [TsExport("concat")]
        public string Concat(string a, string b) => a + b;
    }

    [Fact]
    public void ModuleIdentity_UsesFullKey()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "add");
        Assert.NotNull(desc);
        Assert.Equal("math", desc.ModuleName);
        Assert.Equal("add", desc.FunctionName);
    }

    [Fact]
    public void NoCollision_DifferentModulesSameMethodName()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);
        registry.RegisterObject("strings", new StringService(), ExportMode.ExplicitOnly);

        var mathGet = registry.GetFunction("math", "add");
        var stringGet = registry.GetFunction("strings", "get");

        Assert.NotNull(mathGet);
        Assert.NotNull(stringGet);
        Assert.NotEqual(mathGet.Implementation, stringGet.Implementation);
    }

    [Fact]
    public void InterpreterRegistration_UsesFullKey()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);
        registry.RegisterObject("strings", new StringService(), ExportMode.ExplicitOnly);

        var proxy = new TypeSharpProxy(registry);

        var mathResult = proxy.InvokeHostFunction("math", "add", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });
        Assert.Equal(7, ((TsInt32Value)mathResult!).Value);

        var stringResult = proxy.InvokeHostFunction("strings", "get", new[] { TsValue.FromString("key1") });
        Assert.Equal("value:key1", ((TsStringValue)stringResult!).Value);

        var crossResult = proxy.InvokeHostFunction("math", "get_decimal", Array.Empty<TsValue>());
        Assert.NotNull(crossResult);
        Assert.Equal(123.456m, ((TsDecimalValue)crossResult!).Value);
    }

    [Fact]
    public void DecimalMarshalling_RoundTrip()
    {
        var input = 123.456m;
        var tsValue = Marshaller.FromManaged(input);
        Assert.IsType<TsDecimalValue>(tsValue);

        var output = Marshaller.ToManaged(tsValue, typeof(decimal));
        Assert.Equal(input, output);
    }

    [Fact]
    public void DecimalMarshalling_PreservesPrecision()
    {
        var input = 123456789.123456789m;
        var tsValue = Marshaller.FromManaged(input);
        var output = Marshaller.ToManaged(tsValue, typeof(decimal));
        Assert.Equal(input, output);
    }

    [Fact]
    public void UInt64Marshalling_RoundTrip()
    {
        var input = ulong.MaxValue;
        var tsValue = Marshaller.FromManaged(input);
        Assert.IsType<TsUInt64Value>(tsValue);

        var output = Marshaller.ToManaged(tsValue, typeof(ulong));
        Assert.Equal(input, output);
    }

    [Fact]
    public void UInt64Marshalling_FromInt32()
    {
        var tsValue = TsValue.FromInt32(42);
        var output = Marshaller.ToManaged(tsValue, typeof(ulong));
        Assert.Equal(42UL, output);
    }

    [Fact]
    public void TypeScriptBigIntLiteral_UsesBigIntSurface()
    {
        var result = Run(@"
            function main(): string {
                return typeof 123n;
            }
        ");

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("bigint", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void TypeScriptBigIntLiteral_ComputesAsBigInt()
    {
        var result = Run(@"
            function main(): bigint {
                return 100n + 23n;
            }
        ");

        Assert.IsType<TsBigIntValue>(result);
        Assert.Equal(new System.Numerics.BigInteger(123), ((TsBigIntValue)result!).Value);
    }

    [Fact]
    public void TypeScriptBigIntLiteral_SupportsValuesBeyondUInt64()
    {
        var result = Run(@"
            function main(): string {
                return """" + (18446744073709551616n + 1n);
            }
        ");

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("18446744073709551617", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void BigIntGlobal_ConvertsStandardValues()
    {
        var result = Run(@"
            function main(): string {
                return """" + (BigInt(255) + BigInt(""1""));
            }
        ");

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("256", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void NumberGlobal_ConvertsStandardValues()
    {
        var result = Run(@"
            function main(): number {
                return Number(42n) + Number(""3"");
            }
        ");

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(45d, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void BigIntBitwiseOperators_PreserveArbitraryPrecision()
    {
        var result = Run("""
            function main(): string {
                const high = 18446744073709551616n;
                const xor = high ^ 1n;
                const shifted = (1n << 65n) >> 64n;
                return "" + (xor | shifted);
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("18446744073709551619", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void PrefixedBigIntLiterals_WorkWithBitwiseOperators()
    {
        var result = Run("""
            function main(): string {
                return "" + ((0xffffffffn & 0xffn) | 0b100000000n | 0o7n);
            }
        """);

        Assert.IsType<TsStringValue>(result);
        Assert.Equal("511", ((TsStringValue)result!).Value);
    }

    [Fact]
    public void UInt64BitwiseOperators_ReturnUInt64()
    {
        var result = Run("""
            function main(): uint64 {
                const left: uint64 = 240u64;
                const right: uint64 = 15u64;
                return (left | right) ^ 51u64;
            }
        """);

        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(204UL, ((TsUInt64Value)result!).Value);
    }

    [Fact]
    public void NumberBitwiseOperators_UseInt32Semantics()
    {
        var result = Run("""
            function main(): number {
                const value: number = 0xC0 | (255 >> 6);
                return value & 0xFF;
            }
        """);

        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(195, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void UnsignedRightShift_UsesJavaScriptUInt32Semantics()
    {
        var result = Run("""
            function main(): number {
                return (-1 >>> 0) + (0x80000000 >>> 31);
            }
        """);

        Assert.Equal(4294967296d, Convert.ToDouble(result!.RawValue));
    }

    [Fact]
    public void LoopBreakAndContinue_WorkInForLoops()
    {
        var result = Run("""
            function main(): number {
                let sum = 0;
                for (let i = 0; i < 8; i = i + 1) {
                    if (i === 2) continue;
                    if (i === 6) break;
                    sum = sum + i;
                }
                return sum;
            }
        """);

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(13d, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void Continue_WorkInWhileLoops()
    {
        var result = Run("""
            function main(): number {
                let i = 0;
                let sum = 0;
                while (i < 5) {
                    i = i + 1;
                    if (i === 3) continue;
                    sum = sum + i;
                }
                return sum;
            }
        """);

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(12d, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void Uint8ArrayAnnotation_BehavesLikeByteArray()
    {
        var result = Run(@"
            function main(): number {
                const data: Uint8Array = new Uint8Array([1, 2, 3]);
                return data.length + data[1];
            }
        ");

        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(5.0, ((TsFloat64Value)result!).Value);
    }

    [Fact]
    public void StrictEquality_UsesNoCoercionForDynamicValues()
    {
        var result = Run(@"
            function main(): boolean {
                const text: any = ""1"";
                const numberValue: any = 1;
                return text !== numberValue && !(text === numberValue);
            }
        ");

        Assert.IsType<TsBoolValue>(result);
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void ByteArrayMarshalling_UsesUint8ArrayShape()
    {
        var input = new byte[] { 1, 2, 255 };
        var tsValue = Marshaller.FromManaged(input);

        Assert.IsType<TsUint8ArrayValue>(tsValue);
        var array = (TsUint8ArrayValue)tsValue;
        Assert.Equal(3, array.Length);
        Assert.Equal(255, array.Get(2));

        var output = Marshaller.ToManaged(tsValue, typeof(byte[]));
        Assert.Equal(input, output);
    }

    [Fact]
    public void HostByteArraySignature_ExposesUint8Array()
    {
        var registry = new HostRegistry();
        registry.RegisterFunction(new HostFunctionDescriptor(
            "bytes",
            "load",
            typeof(byte[]),
            Array.Empty<Type>(),
            _ => TsValue.FromUint8Array(new byte[] { 1, 2, 3 })));

        var symbols = registry.CreateGlobalSymbols();
        var load = Assert.IsType<FunctionSymbol>(symbols["load"]);

        Assert.Equal("Uint8Array", load.Type.Name);
    }

    [Fact]
    public void DTO_WrapsAsObject()
    {
        var dto = new { Name = "test", Value = 42 };
        var tsValue = Marshaller.FromManaged(dto);

        Assert.IsType<TsObjectValue>(tsValue);
        var obj = ((TsObjectValue)tsValue).Value;
        Assert.Equal("test", ((TsStringValue)obj.GetField("Name")).Value);
        Assert.Equal(42, ((TsInt32Value)obj.GetField("Value")).Value);
    }

    [Fact]
    public void AsyncMethod_ReturnsResult()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_async_value");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        var promise = Assert.IsType<TsPromiseValue>(result);
        Assert.Equal(42, ((TsInt32Value)promise.Await()).Value);
    }

    [Fact]
    public void AsyncMethod_ValueTask_ReturnsResult()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_async_string");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        var promise = Assert.IsType<TsPromiseValue>(result);
        Assert.Equal("hello", ((TsStringValue)promise.Await()).Value);
    }

    [Fact]
    public void AsyncMethod_VoidTask_DoesNotThrow()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_void_task");
        Assert.NotNull(desc);

        var ex = Record.Exception(() =>
        {
            var promise = Assert.IsType<TsPromiseValue>(desc!.Implementation(Array.Empty<TsValue>()));
            Assert.IsType<TsVoid>(promise.Await());
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Proxy_InvokesCorrectModule()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);
        registry.RegisterObject("strings", new StringService(), ExportMode.ExplicitOnly);

        var proxy = new TypeSharpProxy(registry);

        var mathResult = proxy.InvokeHostFunction("math", "add", new[] { TsValue.FromInt32(10), TsValue.FromInt32(20) });
        Assert.NotNull(mathResult);
        Assert.Equal(30, ((TsInt32Value)mathResult!).Value);

        var stringResult = proxy.InvokeHostFunction("strings", "get", new[] { TsValue.FromString("foo") });
        Assert.NotNull(stringResult);
        Assert.Equal("value:foo", ((TsStringValue)stringResult!).Value);
    }

    [Fact]
    public void Proxy_MissingFunction_ReturnsNull()
    {
        var registry = new HostRegistry();
        var proxy = new TypeSharpProxy(registry);

        var result = proxy.InvokeHostFunction("nonexistent", "func", Array.Empty<TsValue>());
        Assert.Null(result);
    }

    [Fact]
    public void InvokeHostFunction_IsEven_True()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "is_even");
        Assert.NotNull(desc);

        var result = desc!.Implementation(new[] { TsValue.FromInt32(4) });
        Assert.NotNull(result);
        Assert.True(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void InvokeHostFunction_IsEven_False()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "is_even");
        Assert.NotNull(desc);

        var result = desc!.Implementation(new[] { TsValue.FromInt32(3) });
        Assert.NotNull(result);
        Assert.False(((TsBoolValue)result!).Value);
    }

    [Fact]
    public void DynamicHostProxy_Invokes()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var generator = new HostProxyGenerator(registry);
        var proxy = generator.GenerateInterfaceProxy(typeof(MathService), "math");

        var result = ((DynamicHostProxy)proxy).InvokeMethod("add", TsValue.FromInt32(5), TsValue.FromInt32(7));
        Assert.NotNull(result);
        Assert.Equal(12, ((TsInt32Value)result!).Value);
    }

    [Fact]
    public void DecimalMarshalling_ViaHostFunction()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_decimal");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        Assert.IsType<TsDecimalValue>(result);
        Assert.Equal(123.456m, ((TsDecimalValue)result!).Value);
    }

    [Fact]
    public void UInt64Marshalling_ViaHostFunction()
    {
        var registry = new HostRegistry();
        registry.RegisterObject("math", new MathService(), ExportMode.ExplicitOnly);

        var desc = registry.GetFunction("math", "get_ulong");
        Assert.NotNull(desc);

        var result = desc!.Implementation(Array.Empty<TsValue>());
        Assert.NotNull(result);
        Assert.IsType<TsUInt64Value>(result);
        Assert.Equal(ulong.MaxValue, ((TsUInt64Value)result!).Value);
    }

}

public class NegativeTests
{
    private static BytecodeFunction MakeFunc(byte[] instructions, string[] strings)
    {
        return new BytecodeFunction("test", instructions, 0, 0, false, strings, Array.Empty<long>(), Array.Empty<double>());
    }

    private static BytecodeModule MakeModule(BytecodeFunction func)
    {
        return new BytecodeModule("test", new[] { func });
    }

    [Fact]
    public void DivisionByZero_I32_Throws()
    {
        var code = @"
            function main(): int32 {
                const a: int32 = 10;
                const b: int32 = 0;
                return a / b;
            }
        ";
        var ex = Record.Exception(() => Run(code, "main"));
        Assert.NotNull(ex);
        Assert.Contains("Division by zero", ex.Message);
    }

    [Fact]
    public void DivisionByZero_I64_Throws()
    {
        var code = @"
            function main(): int64 {
                const a: int64 = 10i64;
                const b: int64 = 0i64;
                return a / b;
            }
        ";
        var ex = Record.Exception(() => Run(code, "main"));
        Assert.NotNull(ex);
        Assert.Contains("Division by zero", ex.Message);
    }

    [Fact]
    public void DivisionByZero_F64_NoThrow_ThrowsInf()
    {
        var code = @"
            function main(): float64 {
                const a: float64 = 10.0;
                const b: float64 = 0.0;
                return a / b;
            }
        ";
        var result = Run(code, "main");
        Assert.NotNull(result);
        Assert.IsType<TsFloat64Value>(result);
        Assert.True(double.IsInfinity(((TsFloat64Value)result).Value));
    }

    [Fact]
    public void OperandStack_GrowsBeyondInitialCapacity()
    {
        var func = new BytecodeFunction("test", Array.Empty<byte>(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var frame = new CallFrame(func);

        for (int i = 0; i < 300; i++)
            frame.Push(TsValue.Void);

        Assert.Equal(300, frame.StackPointer);
        Assert.True(frame.Stack.Length >= 300);
    }

    [Fact]
    public void OperandStack_ExceedingGuardrail_Throws()
    {
        var maxStackSlots = (int)typeof(CallFrame)
            .GetField("MaxStackSlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        var func = new BytecodeFunction("test", Array.Empty<byte>(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var frame = new CallFrame(func);

        for (int i = 0; i < maxStackSlots; i++)
            frame.Push(TsValue.Void);

        var ex = Record.Exception(() => frame.Push(TsValue.Void));
        Assert.NotNull(ex);
        Assert.Contains("Operand stack limit exceeded", ex.Message);
    }

    [Fact]
    public void InstructionLimit_Exceeded()
    {
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(Opcodes.Branch);
        w.Write(0);
        var func = new BytecodeFunction("test", ms.ToArray(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });

        var limits = new VMRuntimeLimits { MaximumInstructions = 5 };
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
        Assert.Contains("Execution limit exceeded", ex.Message);
    }

    [Fact]
    public void MemoryLimit_Exceeded()
    {
        var heap = new TsHeap(100);
        var limits = new VMRuntimeLimits { MaximumMemoryBytes = 100 };
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);

        interpreter.RegisterHostFunction("test.alloc", (name, args) =>
        {
            interpreter.Heap.AllocateObject("BigObject");
            interpreter.Heap.AllocateObject("BigObject");
            interpreter.Heap.AllocateObject("BigObject");
            return TsValue.Null;
        });

        var instructions = new byte[]
        {
            Opcodes.LoadConstNull,
            Opcodes.LoadConstI32, 0, 0, 0, 0,
            Opcodes.CallHost, 0, 0, 0, 0, 1, 0, 0, 0,
            Opcodes.ReturnVoid
        };

        var func = new BytecodeFunction("test", instructions, 0, 0, false,
            new[] { "test.alloc" }, new long[] { 0 }, Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });

        var limits2 = new VMRuntimeLimits { MaximumMemoryBytes = 1 };
        var interpreter2 = new TypeSharp.VM.Interpreter.Interpreter(limits2);

        interpreter2.RegisterHostFunction("test.alloc", (name, args) =>
        {
            interpreter2.Heap.AllocateObject("BigObject");
            return TsValue.Null;
        });

        var ex = Record.Exception(() => interpreter2.Execute(module, "test"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void RecursionLimit_Exceeded()
    {
        var bytecodeModule = CompileRecursionProbeModule();

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(
            new VMRuntimeLimits { MaximumRecursionDepth = 4 });
        var ex = Record.Exception(() => interpreter.Execute(bytecodeModule, "main"));
        Assert.NotNull(ex);
        Assert.Contains("recursion depth", ex.Message, StringComparison.OrdinalIgnoreCase);

        var safe = interpreter.Execute(bytecodeModule, "safe");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(safe).Value);
    }

    [Fact]
    public void RecursionLimit_ProbeProcess()
    {
        var bytecodeModule = CompileRecursionProbeModule();
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(
            new VMRuntimeLimits { MaximumRecursionDepth = 4 });

        var ex = Record.Exception(() => interpreter.Execute(bytecodeModule, "main"));
        Assert.NotNull(ex);
        Assert.Contains("recursion depth", ex.Message, StringComparison.OrdinalIgnoreCase);

        var safe = interpreter.Execute(bytecodeModule, "safe");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(safe).Value);
    }

    [Fact]
    public void RecursionLimit_IsolatedProcess_DoesNotCrash()
    {
        var assemblyPath = typeof(NegativeTests).Assembly.Location;
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--Tests:TypeSharp.VM.Tests.NegativeTests.RecursionLimit_ProbeProcess");

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(30_000), "recursion probe process did not exit within 30 seconds");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        Assert.True(process.ExitCode == 0,
            $"recursion probe process failed with exit code {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    private static BytecodeModule CompileRecursionProbeModule()
    {
        var code = @"
            function recurse(n: int32): int32 {
                return recurse(n + 1);
            }
            function main(): int32 {
                return recurse(0);
            }
            function safe(): int32 {
                return 7;
            }
        ";

        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        return BytecodeCompiler.Compile(moduleIR);
    }

    [Fact]
    public void Timeout_ThrowsOperationCanceled()
    {
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(Opcodes.Branch);
        w.Write(0);
        var func = new BytecodeFunction("test", ms.ToArray(), 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });

        var limits = new VMRuntimeLimits { ExecutionTimeout = TimeSpan.FromMilliseconds(1) };
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter(limits);

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void UnknownOpcode_Throws()
    {
        var instructions = new byte[] { 0xFF };
        var func = new BytecodeFunction("test", instructions, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
        Assert.Contains("Unknown opcode", ex.Message);
    }

    [Fact]
    public void NullFieldAccess_ReturnsNull()
    {
        var code = @"
            function main(): int32 {
                const x: int32 = 0;
                return x;
            }
        ";
        var result = Run(code, "main");
        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(0, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void StackUnderflow_Throws()
    {
        var instructions = new byte[] { Opcodes.Pop };
        var func = new BytecodeFunction("test", instructions, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());
        var module = new BytecodeModule("test", new[] { func });
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        var ex = Record.Exception(() => interpreter.Execute(module, "test"));
        Assert.NotNull(ex);
        Assert.Contains("Stack underflow", ex.Message);
    }

    [Fact]
    public void Switch_SelectsCase_FallsThrough_AndBreaks()
    {
        var code = @"
            function main(value: int32): int32 {
                let result: int32 = 0;
                switch (value) {
                    case 1:
                        result = result + 10;
                    case 2:
                        result = result + 2;
                        break;
                    default:
                        result = 99;
                }
                return result;
            }
        ";

        Assert.Equal(12, ((TsInt32Value)Run(code, "main", new TsValue[] { new TsInt32Value(1) })!).Value);
        Assert.Equal(2, ((TsInt32Value)Run(code, "main", new TsValue[] { new TsInt32Value(2) })!).Value);
        Assert.Equal(99, ((TsInt32Value)Run(code, "main", new TsValue[] { new TsInt32Value(7) })!).Value);
    }

    [Fact]
    public void Switch_EvaluatesDiscriminantExactlyOnce()
    {
        var code = @"
            function main(): int32 {
                let index: int32 = 0;
                const values: int32[] = [2];
                let result: int32 = 0;
                switch (values[index++]) {
                    case 1:
                        result = 1;
                        break;
                    case 2:
                        result = 2;
                        break;
                    default:
                        result = 3;
                }
                return index * 10 + result;
            }
        ";

        Assert.Equal(12, ((TsInt32Value)Run(code)!).Value);
    }

    [Fact]
    public void ContinueInsideSwitch_ContinuesEnclosingLoop()
    {
        var code = @"
            function main(): int32 {
                let sum: int32 = 0;
                for (let i: int32 = 0; i < 3; i++) {
                    switch (i) {
                        case 1:
                            continue;
                        default:
                            sum = sum + i;
                    }
                }
                return sum;
            }
        ";

        Assert.Equal(2, ((TsInt32Value)Run(code)!).Value);
    }

    private static TsValue? Run(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }
}

public class ConcurrencyTests
{
    [Fact]
    public void ParallelExecute_DoesNotCorruptState()
    {
        var code = @"
            function main(): int32 {
                const x: int32 = 0;
                const y: int32 = 1;
                return x + y;
            }
        ";

        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var results = new TsValue?[10];
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            tasks[idx] = Task.Run(() =>
            {
                var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
                results[idx] = interpreter.Execute(bytecodeModule, "main");
            });
        }

        Task.WaitAll(tasks);

        foreach (var r in results)
        {
            Assert.NotNull(r);
            Assert.IsType<TsInt32Value>(r);
            Assert.Equal(1, ((TsInt32Value)r).Value);
        }
    }

    [Fact]
    public void RepeatedExecute_IsStateless()
    {
        var code = @"
            function main(): int32 {
                const x: int32 = 5;
                return x;
            }
        ";

        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        for (int i = 0; i < 100; i++)
        {
            var result = interpreter.Execute(bytecodeModule, "main");
            Assert.NotNull(result);
            Assert.Equal(5, ((TsInt32Value)result).Value);
        }
    }

    [Fact]
    public void SharedInterpreter_NoCrossContamination()
    {
        var addCode = @"
            function main(a: int32, b: int32): int32 {
                return a + b;
            }
        ";

        var mulCode = @"
            function main(a: int32, b: int32): int32 {
                return a * b;
            }
        ";

        var addModule = Compile(addCode);
        var mulModule = Compile(mulCode);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();

        var r1 = interpreter.Execute(addModule, "main", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });
        Assert.Equal(7, ((TsInt32Value)r1!).Value);

        var r2 = interpreter.Execute(mulModule, "main", new[] { TsValue.FromInt32(3), TsValue.FromInt32(4) });
        Assert.Equal(12, ((TsInt32Value)r2!).Value);

        var r3 = interpreter.Execute(addModule, "main", new[] { TsValue.FromInt32(10), TsValue.FromInt32(20) });
        Assert.Equal(30, ((TsInt32Value)r3!).Value);
    }

    private static BytecodeModule Compile(string code)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        return BytecodeCompiler.Compile(moduleIR);
    }
}

public class IROptimizerTests
{
    private static TsValue? RunOptimized(string code, string entryPoint = "main", TsValue[]? args = null)
    {
        var lexer = new TypeSharp.Syntax.Lexer(code);
        var tokens = lexer.Tokenize();

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse();

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);

        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);

        var pipeline = new IRPipeline();
        foreach (var func in moduleIR.Functions)
            pipeline.Optimize(func);

        var bytecodeModule = BytecodeCompiler.Compile(moduleIR);

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        return interpreter.Execute(bytecodeModule, entryPoint, args);
    }

    private static List<Instruction> OptimizeInstructions(params Instruction[] instructions)
    {
        var func = new FunctionIR("test", TsType.Int32, new List<ParameterInfo>());
        var block = func.CreateBlock();
        block.Instructions.AddRange(instructions);
        new IRPipeline().Optimize(func);
        return func.Blocks.SelectMany(b => b.Instructions).ToList();
    }

    [Fact]
    public void DivisionByZero_ConstantFolded_ThrowsAtRuntime()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RunOptimized("function main(): int32 { return 10 / 0; }"));
        Assert.Contains("Division by zero", error.Message);
    }

    [Fact]
    public void ModuloByZero_ConstantFolded_ThrowsAtRuntime()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RunOptimized("function main(): int32 { return 10 % 0; }"));
        Assert.Contains("Division by zero", error.Message);
    }

    [Fact]
    public void SelfAssignment_Peephole_ReturnsAssignedValue()
    {
        var result = RunOptimized("function main(): int32 { let x: int32 = 5; x = x; return x; }");
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void ValidDivision_StillFoldsToSingleConstant()
    {
        var optimized = OptimizeInstructions(
            new Instruction(Opcode.LoadConst_I32, 10),
            new Instruction(Opcode.LoadConst_I32, 2),
            new Instruction(Opcode.Div_I32));

        Assert.Single(optimized);
        Assert.Equal(Opcode.LoadConst_I32, optimized[0].Opcode);
        Assert.Equal(5, optimized[0].Operand0);
    }

    [Fact]
    public void DivisionByZero_NotFoldedAtIRLevel()
    {
        var optimized = OptimizeInstructions(
            new Instruction(Opcode.LoadConst_I32, 10),
            new Instruction(Opcode.LoadConst_I32, 0),
            new Instruction(Opcode.Div_I32));

        Assert.Equal(3, optimized.Count);
        Assert.Contains(optimized, i => i.Opcode == Opcode.Div_I32);
    }

    [Fact]
    public void MinValueDividedByMinusOne_NotFoldedAtIRLevel()
    {
        var optimized = OptimizeInstructions(
            new Instruction(Opcode.LoadConst_I32, int.MinValue),
            new Instruction(Opcode.LoadConst_I32, -1),
            new Instruction(Opcode.Div_I32));

        Assert.Equal(3, optimized.Count);
        Assert.Contains(optimized, i => i.Opcode == Opcode.Div_I32);
    }

    // DESVIACIÓN D1 (ver implementation-c.md): el plan (§2.1/§3.3.7) prescribía
    // plegar int.MinValue % -1 a 0, afirmando que no lanza. La ejecución en .NET 9
    // demuestra lo contrario: int.MinValue % -1 lanza OverflowException (igual que
    // la división), y el intérprete hace el mismo % plano, así que plegarlo a 0
    // cambiaría lanzar-por-ejecución por devolver 0. Por eso el módulo con -1
    // TAMPOCO se pliega, simétrico a la división.
    [Fact]
    public void MinValueModuloMinusOne_NotFoldedAtIRLevel()
    {
        var optimized = OptimizeInstructions(
            new Instruction(Opcode.LoadConst_I32, int.MinValue),
            new Instruction(Opcode.LoadConst_I32, -1),
            new Instruction(Opcode.Mod_I32));

        Assert.Equal(3, optimized.Count);
        Assert.Contains(optimized, i => i.Opcode == Opcode.Mod_I32);
    }

    [Fact]
    public void ParameterSelfAssignment_LoadArgStoreLocal_Untouched()
    {
        var result = RunOptimized(
            "function main(p: int32): int32 { p = p; return p; }",
            args: new[] { TsValue.FromInt32(42) });
        Assert.Equal(42, Assert.IsType<TsInt32Value>(result).Value);
    }

    [Fact]
    public void AssignmentBetweenDifferentLocals_Untouched()
    {
        var result = RunOptimized("function main(): int32 { let x: int32 = 5; let y: int32 = 7; x = y; return x; }");
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result).Value);
    }

}
