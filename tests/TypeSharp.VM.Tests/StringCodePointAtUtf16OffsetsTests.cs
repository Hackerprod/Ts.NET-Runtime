using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringCodePointAtUtf16OffsetsTests(ITestOutputHelper output)
{
    private const string Witness = "function main(text: string, index: number): any { return text.codePointAt(index); }";
    private const string Typed = "function main(text: string, index: number): number | undefined { return text.codePointAt(index); }";
    private const string Control = "function main(text: string, index: number): number { return text.charCodeAt(index); }";

    [Fact]
    public void CodePointAt_UsesUtf16Offsets()
    {
        const string row = nameof(CodePointAt_UsesUtf16Offsets);
        var witness = Compile(row, "witness", Witness, "codePointAt");
        Run(row, "witness", Witness, witness, 1, 128512);

        var typed = Compile(row, "typed", Typed, "codePointAt");
        Run(row, "typed", Typed, typed, 0, 65);
        Run(row, "typed", Typed, typed, 1, 128512);
        Run(row, "typed", Typed, typed, 2, 56832);
        Run(row, "typed", Typed, typed, 3, 90);
    }

    [Fact]
    public void CharCodeAt_RetainsCodeUnitAtHighSurrogate()
    {
        const string row = nameof(CharCodeAt_RetainsCodeUnitAtHighSurrogate);
        var control = Compile(row, "control", Control, "charCodeAt");
        Run(row, "control", Control, control, 1, 55357);
    }

    private static void AssertNumberOrUndefined(TsType type)
    {
        var members = Assert.IsType<TsUnionType>(type).Types;
        Assert.Equal(2, members.Count);
        Assert.Single(members, member => member.Equals(TsType.Number));
        Assert.Single(members, member => member.Equals(TsType.Undefined));
    }

    private static void AssertDeclaredReturn(string phase, TsType type)
    {
        if (phase == "typed")
            AssertNumberOrUndefined(type);
        else
            Assert.Equal(phase == "control" ? TsType.Number : TsType.Any, type);
    }

    private (ModuleIR Ir, FunctionIR Main, TsType CallReturn, string[] Ops, string Target, int Argc)
        Compile(string row, string phase, string source, string methodName)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All.Select(d => new
        {
            severity = d.Severity.ToString(), code = d.Code.ToString(), message = d.Message, offset = d.Location.Offset
        }).ToArray();
        output.WriteLine("C151_BIND " + JsonSerializer.Serialize(new
        {
            row, phase, source, parserCount = parser.Diagnostics.Count, binderCount = diagnostics.Length, diagnostics
        }));
        Assert.True(diagnostics.Length == 0, $"C151_BINDER_FAILURE {row} {phase}");

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        AssertDeclaredReturn(phase, function.Symbol.Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        var access = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal(methodName, method.Name);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal(TsType.String, access.Object.Type);
        Assert.Equal(TsType.Number, Assert.Single(boundCall.Arguments).Type);
        if (phase == "control")
        {
            Assert.Equal(TsType.Number, boundCall.Type);
            Assert.Equal(TsType.Number, method.Type);
        }
        else
        {
            AssertNumberOrUndefined(boundCall.Type);
            AssertNumberOrUndefined(method.Type);
        }

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        AssertDeclaredReturn(phase, main.ReturnType);
        Assert.Collection(main.Parameters,
            parameter => { Assert.Equal("text", parameter.Name); Assert.Equal(TsType.String, parameter.Type); Assert.False(parameter.IsRest); },
            parameter => { Assert.Equal("index", parameter.Name); Assert.Equal(TsType.Number, parameter.Type); Assert.False(parameter.IsRest); });
        var instructions = Assert.Single(main.Blocks).Instructions;
        var call = Assert.Single(instructions, item => item.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        var ops = instructions.Select(item => item.Opcode switch
        {
            Opcode.LoadArg => $"{item.Opcode}:{item.Operand0}",
            Opcode.Call => $"{item.Opcode}:{item.OperandObject}/{item.Operand1}",
            _ => item.Opcode.ToString()
        }).ToArray();
        Assert.Equal(new[] { "LoadArg:0", "LoadArg:1", $"Call:String::{methodName}/2", "Return" }, ops);
        return (ir, main, boundCall.Type, ops, target, call.Operand1);
    }

    private void Run(string row, string phase, string source,
        (ModuleIR Ir, FunctionIR Main, TsType CallReturn, string[] Ops, string Target, int Argc) program,
        int position, int expected)
    {
        var receiver = TsValue.FromString("A\U0001F600Z");
        var index = TsValue.FromFloat64(position);
        var bytecode = BytecodeCompiler.Compile(program.Ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsInt32Value>(vm.Execute(bytecode, "main", [receiver, index]));
        Assert.Equal(TsValueType.Int32, result.ValueType);
        var actual = Assert.IsType<int>(result.RawValue);
        Assert.Equal(expected, actual);
        output.WriteLine("C151_RUN " + JsonSerializer.Serialize(new
        {
            row, phase, source, entry = program.Main.Name,
            parameters = program.Main.Parameters.Select(p => new { name = p.Name, type = p.Type.Name, rest = p.IsRest }),
            declaredReturn = program.Main.ReturnType.Name, callReturn = program.CallReturn.Name,
            receiverUnits = string.Join("+", Assert.IsType<TsStringValue>(receiver).Value.Select(
                unit => ((ushort)unit).ToString("X4", CultureInfo.InvariantCulture))),
            offset = Assert.IsType<TsFloat64Value>(index).Value,
            ops = program.Ops, target = program.Target, argc = program.Argc, wrapper = result.GetType().Name,
            tag = result.ValueType.ToString(), expected, actual
        }));
    }
}
