using TypeSharp.IR;
using TypeSharp.IR.ControlFlow;
using TypeSharp.Semantics.TypeSystem;
using Xunit;

namespace TypeSharp.Tests;

public sealed class ControlFlowGraphRebuildTests
{
    private const string Original = "0:S[1]P[] 1:S[3]P[0] 2:S[3]P[] 3:S[]P[1,2] R1=True R2=False";
    private const string Retargeted = "0:S[2]P[] 1:S[3]P[] 2:S[3]P[0] 3:S[]P[1,2] R1=False R2=True";
    private const string Conditional = "0:S[1,1]P[] 1:S[3]P[0,0] 2:S[3]P[] 3:S[]P[1,2] R1=True R2=False";

    [Fact]
    public void FirstBuildCreatesExactEdges() =>
        AssertGraph("first-build", new ControlFlowGraph(CreateFunction()), Original);

    [Fact]
    public void RebuildDoesNotDuplicateEdges()
    {
        var function = CreateFunction();
        var original = new ControlFlowGraph(function);
        var rebuilt = new ControlFlowGraph(function);
        Assert.NotSame(original.Exit, rebuilt.Exit);
        AssertGraph("repeat", rebuilt, Original);
    }

    [Fact]
    public void RebuildReplacesRetargetedEdges()
    {
        var function = CreateFunction();
        var original = new ControlFlowGraph(function);
        function.Blocks[0].Instructions[0] = new Instruction(Opcode.Branch, 2);
        var rebuilt = new ControlFlowGraph(function);
        Assert.NotSame(original.Exit, rebuilt.Exit);
        AssertGraph("retarget", rebuilt, Retargeted);
    }

    [Fact]
    public void FirstBuildPreservesCoincidentConditionalEdges() =>
        AssertGraph("conditional", new ControlFlowGraph(CreateFunction(conditional: true)), Conditional);

    private static FunctionIR CreateFunction(bool conditional = false)
    {
        var function = new FunctionIR("test", TsType.Void, new());
        var entry = function.CreateBlock();
        if (conditional) entry.Instructions.Add(new Instruction(Opcode.Nop));
        entry.Instructions.Add(new Instruction(conditional ? Opcode.BranchTrue : Opcode.Branch, 1));
        function.CreateBlock().Instructions.Add(new Instruction(Opcode.ReturnVoid));
        function.CreateBlock().Instructions.Add(new Instruction(Opcode.ReturnVoid));
        return function;
    }

    private static void AssertGraph(string id, ControlFlowGraph graph, string expected)
    {
        Assert.Same(graph.Blocks[0], graph.Entry);
        Assert.Same(graph.Blocks[3], graph.Exit);
        var edges = string.Join(" ", graph.Blocks.Select(block =>
            $"{block.Id}:S[{string.Join(",", block.Successors)}]P[{string.Join(",", block.Predecessors)}]"));
        var actual = $"{edges} R1={graph.IsReachable(graph.Blocks[1])} R2={graph.IsReachable(graph.Blocks[2])}";
        Assert.True(actual == expected, $"{id}: expected {expected}; actual {actual}");
    }
}
