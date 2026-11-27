using System.Buffers.Binary;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class SetCopyConstructorTests(ITestOutputHelper output)
{
    private const string Rejection = "Set constructor expects an array of values";
    private const string Source = """
        function numbers(): Set<int32> { return new Set<int32>([3, 1, 3, 2]); }
        function copyNumbers(source: Set<int32>): Set<int32> { return new Set<int32>(source); }
        function makeMember(): any { return { value: 7 }; }
        function makeObjects(x: any, y: any): Set<any> { return new Set<any>([x, x, y]); }
        function copyObjects(source: Set<any>): Set<any> { return new Set<any>(source); }
        function snapshot(source: Set<int32>): int32[] { return [...source]; }
        function mutate(source: Set<int32>, remove: int32, append: int32): void {
            source.delete(remove);
            source.add(append);
        }
        function makeMap(): Map<any, any> { return new Map<any, any>(); }
        function rejectMap(source: Map<any, any>): Set<any> { return new Set<any>(source); }
        function omitted(): Set<int32> { return new Set<int32>(); }
        function nullInput(): Set<int32> { return new Set<int32>(null); }
        function undefinedInput(): Set<int32> { return new Set<int32>(undefined); }
        function stringInput(): Set<string> { return new Set<string>("banana"); }
        """;

    [Fact]
    public void NativeCopyPreservesOrderAndOwnsSeparateMembership()
    {
        var module = Compile("copyNumbers");
        var vm = new Vm();
        var source = Set(vm.Execute(module, "numbers"));
        Snapshot(vm, module, source, 3, 1, 2);
        output.WriteLine("SETUP copyNumbers source=Set[Int32:3,Int32:1,Int32:2]");
        var destination = Copy(vm, module, "copyNumbers", source);
        Snapshot(vm, module, source, 3, 1, 2);
        Snapshot(vm, module, destination, 3, 1, 2);

        vm.Execute(module, "mutate", [source, new TsInt32Value(1), new TsInt32Value(9)]);
        Snapshot(vm, module, source, 3, 2, 9);
        Snapshot(vm, module, destination, 3, 1, 2);
        vm.Execute(module, "mutate", [destination, new TsInt32Value(3), new TsInt32Value(8)]);
        Snapshot(vm, module, destination, 1, 2, 8);
        Snapshot(vm, module, source, 3, 2, 9);
        output.WriteLine("RESULT copyNumbers order=3,1,2 source=3,2,9 destination=1,2,8 distinct=true");
    }

    [Fact]
    public void NativeCopyIsShallowAndPreservesDistinctObjectKeys()
    {
        var module = Compile("copyObjects");
        var vm = new Vm();
        var first = Assert.IsType<TsObjectValue>(vm.Execute(module, "makeMember"));
        var second = Assert.IsType<TsObjectValue>(vm.Execute(module, "makeMember"));
        var lookalike = Assert.IsType<TsObjectValue>(vm.Execute(module, "makeMember"));
        Assert.NotSame(first.Value, second.Value);
        Assert.NotSame(first.Value, lookalike.Value);
        Assert.NotSame(second.Value, lookalike.Value);
        foreach (var obj in new[] { first, second, lookalike })
            Assert.Equal(7, Assert.IsType<TsInt32Value>(obj.Value.GetField("value")).Value);
        var source = Set(vm.Execute(module, "makeObjects", [first, second]));
        Check(source);
        output.WriteLine("SETUP copyObjects source=Set[Object,distinct Object] lookalike=false");
        var destination = Copy(vm, module, "copyObjects", source);
        Check(destination);
        Check(source);
        output.WriteLine("RESULT copyObjects same-element-objects=true lookalike=false");

        void Check(TsSetValue set)
        {
            Assert.Equal(2, set.Value.Count);
            Assert.Same(first.Value, Assert.IsType<TsObjectValue>(set.Value.GetAt(0)).Value);
            Assert.Same(second.Value, Assert.IsType<TsObjectValue>(set.Value.GetAt(1)).Value);
            Assert.True(set.Value.Contains(first) && set.Value.Contains(second));
            Assert.True(!set.Value.Contains(lookalike));
        }
    }

    [Fact]
    public void EmptyNativeCopyOwnsSeparateMembership()
    {
        var module = Compile("copyNumbers");
        var vm = new Vm();
        var source = Set(vm.Execute(module, "omitted"));
        Snapshot(vm, module, source);
        output.WriteLine("SETUP copyEmpty source=Set[]");
        var destination = Copy(vm, module, "copyNumbers", source, "copyEmpty");
        Snapshot(vm, module, source);
        Snapshot(vm, module, destination);
        output.WriteLine("RESULT copyEmpty source=0 destination=0 distinct=true");
    }

    [Fact]
    public void ExistingConstructorInputsAndMapRejectionAreUnchanged()
    {
        var module = Compile("rejectMap");
        var vm = new Vm();
        foreach (string route in new[] { "omitted", "nullInput", "undefinedInput" })
            Snapshot(vm, module, Set(vm.Execute(module, route)));
        Snapshot(vm, module, Set(vm.Execute(module, "numbers")), 3, 1, 2);
        var strings = Set(vm.Execute(module, "stringInput")).Value.Entries;
        Assert.Equal(new[] { "b", "a", "n" }, strings.Select(entry => Assert.IsType<TsStringValue>(entry).Value).ToArray());
        var map = Assert.IsType<TsMapValue>(vm.Execute(module, "makeMap"));
        Assert.Equal(TsValueType.Map, map.ValueType);
        bool rejected = false;
        try { vm.Execute(module, "rejectMap", [map]); }
        catch (InvalidOperationException error) when (IsNativeRejection(error)) { rejected = true; }
        Assert.True(rejected, "Native Map input must still be rejected by PopulateSet");
        output.WriteLine("RESULT controls omitted/null/undefined=empty array=3,1,2 string=b,a,n Map=PopulateSet rejection");
    }

    private static bool IsNativeRejection(InvalidOperationException error) =>
        error.GetType() == typeof(InvalidOperationException) && error.Message == Rejection
        && error.TargetSite?.Name == "PopulateSet" && error.TargetSite.DeclaringType == typeof(Vm);

    private TsSetValue Copy(Vm vm, BytecodeModule module, string route, TsSetValue source, string? row = null)
    {
        TsValue? value;
        try { value = vm.Execute(module, route, [source]); }
        catch (InvalidOperationException error) when (IsNativeRejection(error))
        {
            string evidence = $"C305_SET_COPY_UNSUPPORTED row={row ?? route}; input=Set; native={error.GetType().FullName}; origin=PopulateSet; message={error.Message}";
            output.WriteLine(evidence);
            Assert.True(false, evidence);
            throw;
        }
        var copy = Set(value);
        Assert.NotSame(source, copy);
        Assert.NotSame(source.Value, copy.Value);
        return copy;
    }

    private static TsSetValue Set(TsValue? raw)
    {
        var set = Assert.IsType<TsSetValue>(raw);
        Assert.Equal(TsValueType.Set, set.ValueType);
        return set;
    }

    private static void Snapshot(Vm vm, BytecodeModule module, TsSetValue set, params int[] expected)
    {
        var spread = Assert.IsType<TsArrayValue>(vm.Execute(module, "snapshot", [set])).Value;
        Assert.Equal(expected.Length, set.Value.Count);
        Assert.Equal(expected.Length, spread.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(!spread.IsHole(i));
            var entry = Assert.IsType<TsInt32Value>(spread.Get(i));
            Assert.Equal(TsValueType.Int32, entry.ValueType);
            Assert.Equal(expected[i], entry.Value);
            Assert.Equal(expected[i], Assert.IsType<TsInt32Value>(set.Value.GetAt(i)).Value);
            Assert.True(set.Value.Contains(new TsInt32Value(expected[i])));
        }
    }

    private BytecodeModule Compile(string route)
    {
        var tokens = new Lexer(Source, "set-copy-constructor.ts").Tokenize();
        Assert.Equal(0, tokens.Count(token => token.Kind == TokenKind.Error));
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var fn = syntax.Members.OfType<FunctionDeclarationSyntax>().Single(f => f.Name == route);
        var parameter = Assert.Single(fn.Parameters);
        Assert.Equal("source", parameter.Name);
        Assert.Equal(route == "rejectMap" ? "Map" : "Set", Assert.IsType<NamedTypeSyntax>(parameter.TypeAnnotation).Name);
        var result = Assert.IsType<NamedTypeSyntax>(fn.ReturnType);
        Assert.Equal("Set", result.Name);
        Assert.Single(result.TypeArguments);
        var statement = Assert.Single(Assert.IsType<BlockStatementSyntax>(fn.Body).Statements);
        var parsedNew = Assert.IsType<NewExpressionSyntax>(Assert.IsType<ReturnStatementSyntax>(statement).Value);
        var target = Assert.IsType<NamedTypeSyntax>(parsedNew.Type);
        Assert.Equal("Set", target.Name);
        Assert.Single(target.TypeArguments);
        Assert.Equal("source", Assert.IsType<IdentifierExpressionSyntax>(Assert.Single(parsedNew.Arguments)).Name);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);
        var boundFn = bound.Members.OfType<BoundFunctionDeclaration>().Single(f => f.Symbol.Name == route);
        var boundReturn = Assert.IsType<BoundReturnStatement>(Assert.Single(Assert.IsType<BoundBlockStatement>(boundFn.Body).Statements));
        var constructor = Assert.IsType<BoundNewExpression>(boundReturn.Value);
        Assert.IsType<TsSetType>(constructor.ConstructedType);
        var argument = Assert.IsType<BoundVariableExpression>(Assert.Single(constructor.Arguments));
        if (route == "rejectMap") Assert.IsType<TsMapType>(argument.Type);
        else Assert.IsType<TsSetType>(argument.Type);
        var ir = new IRGenerator().Generate(bound);
        var ops = ir.Functions.Single(f => f.Name == route).Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.True(!ops.Any(op => op.Opcode is Opcode.Call or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray));
        var ctor = ops.Single(op => op.Opcode == Opcode.NewObject);
        Assert.Equal("Set", Assert.IsType<string>(ctor.OperandObject));
        Assert.Equal(1, ctor.Operand1);
        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);
        var function = module.Functions.Single(f => f.Name == route);
        Assert.Equal(1, function.ParameterCount);
        var code = function.Instructions;
        var offsets = new List<int>();
        for (int i = 0; i < code.Length; i += OpcodeFormats.InstructionSize(code[i])) offsets.Add(i);
        int offset = offsets.Single(i => code[i] == Opcodes.NewObject);
        Assert.Equal("Set", function.StringConstants[BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset + 1, 4))]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset + 5, 4)));
        Assert.True(!offsets.Any(i => code[i] is Opcodes.Call or Opcodes.CallVirt or Opcodes.CallDynamic or Opcodes.CallDynamicArray));
        output.WriteLine($"ROUTE {route} diagnostics=0/0/0 ir=NewObject:Set:1 bytecode=NewObject:Set:1 calls=0 bytes={Convert.ToHexString(code)}");
        return module;
    }
}
