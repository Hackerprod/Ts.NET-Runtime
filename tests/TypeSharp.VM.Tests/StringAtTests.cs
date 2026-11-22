using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class StringAtTests
{
    private const string Witness = "function main(text: string, index: number): any { return text.at(index); }";
    private const string Typed = "function main(text: string, index: number): string | undefined { return text.at(index); }";

    [Fact]
    public void At_UsesRelativeUtf16Indexing()
    {
        var witness = Compile("witness", Witness, typed: false);
        AssertUnit(witness, 0, 'A');

        var typed = Compile("typed", Typed, typed: true);
        AssertUnit(typed, 0, 'A');
        AssertUnit(typed, -1, 'Z');
        AssertUnit(typed, -1.8, 'Z');
        AssertUnit(typed, -2, '\uDE00');
        AssertVoid(typed, 4);
        AssertVoid(typed, -5);

        AssertDeclaration();
    }

    private static ModuleIR Compile(string phase, string source, bool typed)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All.Select(d =>
            $"{d.Severity}/{d.Code} {d.Location.Line}:{d.Location.Column} offset={d.Location.Offset}: {d.Message}").ToArray();
        Assert.True(diagnostics.Length == 0,
            $"String.at {phase} binder diagnostics: count={diagnostics.Length}; {string.Join("; ", diagnostics)}");

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        AssertReturn(function.Symbol.Type, typed);
        Assert.Collection(function.Symbol.Parameters,
            parameter => { Assert.Equal("text", parameter.Name); Assert.Equal(TsType.String, parameter.Type); },
            parameter => { Assert.Equal("index", parameter.Name); Assert.Equal(TsType.Number, parameter.Type); });

        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        var access = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("at", method.Name);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal(TsType.String, access.Object.Type);
        Assert.Equal(TsType.Number, Assert.Single(boundCall.Arguments).Type);
        AssertStringOrUndefined(method.Type);
        AssertStringOrUndefined(boundCall.Type);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        AssertReturn(main.ReturnType, typed);
        Assert.Collection(main.Parameters,
            parameter => { Assert.Equal("text", parameter.Name); Assert.Equal(TsType.String, parameter.Type); Assert.False(parameter.IsRest); },
            parameter => { Assert.Equal("index", parameter.Name); Assert.Equal(TsType.Number, parameter.Type); Assert.False(parameter.IsRest); });

        Assert.Collection(Assert.Single(main.Blocks).Instructions,
            item => { Assert.Equal(Opcode.LoadArg, item.Opcode); Assert.Equal(0, item.Operand0); },
            item => { Assert.Equal(Opcode.LoadArg, item.Opcode); Assert.Equal(1, item.Operand0); },
            item => { Assert.Equal(Opcode.Call, item.Opcode); Assert.Equal("String::at", Assert.IsType<string>(item.OperandObject)); Assert.Equal(2, item.Operand1); },
            item => Assert.Equal(Opcode.Return, item.Opcode));
        return ir;
    }

    private static void AssertReturn(TsType type, bool typed)
    {
        if (typed)
            AssertStringOrUndefined(type);
        else
            Assert.Equal(TsType.Any, type);
    }

    private static void AssertStringOrUndefined(TsType type)
    {
        var members = Assert.IsType<TsUnionType>(type).Types;
        Assert.Equal(2, members.Count);
        Assert.Single(members, member => member.Equals(TsType.String));
        Assert.Single(members, member => member.Equals(TsType.Undefined));
    }

    private static TsValue? Run(ModuleIR program, double position)
    {
        var receiver = Assert.IsType<TsStringValue>(TsValue.FromString("A\U0001F600Z"));
        var index = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(position));
        var bytecode = BytecodeCompiler.Compile(program);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        return vm.Execute(bytecode, "main", [receiver, index]);
    }

    private static void AssertUnit(ModuleIR program, double position, char expected)
    {
        var result = Assert.IsType<TsStringValue>(Run(program, position));
        Assert.Equal(TsValueType.String, result.ValueType);
        Assert.Equal(expected.ToString(), result.Value);
        Assert.Equal(1, result.Value.Length);
        Assert.Equal(expected, result.Value[0]);
    }

    private static void AssertVoid(ModuleIR program, double position)
    {
        var result = Assert.IsType<TsVoid>(Run(program, position));
        Assert.Equal(TsValueType.Void, result.ValueType);
    }

    private static void AssertDeclaration()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TypeSharp.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var lines = File.ReadAllLines(Path.Combine(root.FullName, "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var start = Array.IndexOf(lines, "interface String {");
        Assert.True(start >= 0);
        var end = Array.IndexOf(lines, "}", start + 1);
        Assert.True(end > start);
        Assert.Equal("    at(index: number): string | undefined;", Assert.Single(lines[(start + 1)..end],
            line => line.TrimStart().StartsWith("at(", StringComparison.Ordinal)));
    }
}
