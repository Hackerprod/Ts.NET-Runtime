using System.Collections.Generic;
using System.Linq;
using TypeSharp.IR;
using TypeSharp.IR.ControlFlow;
using TypeSharp.Semantics.TypeSystem;
using Xunit;

namespace TypeSharp.Tests;

public class ControlFlowGraphBranchChainTests
{
    private static FunctionIR Setup(params Instruction[] instructions)
    {
        var f = new FunctionIR("branch-chain", TsType.Void, new List<ParameterInfo>());
        f.CreateBlock().Instructions.AddRange(instructions);
        f.CreateBlock().Instructions.Add(new Instruction(Opcode.ReturnVoid));
        f.CreateBlock().Instructions.Add(new Instruction(Opcode.ReturnVoid));
        return f;
    }

    private static void Check(FunctionIR f, string expected)
    {
        var graph = new ControlFlowGraph(f);
        var actual = string.Join(";", graph.Blocks.Select(b =>
            b.Id + ":S[" + string.Join(",", b.Successors) +
            "]P[" + string.Join(",", b.Predecessors) +
            "]R" + (graph.IsReachable(b) ? "1" : "0")));
        Assert.True(actual == expected, $"expected {expected}; actual {actual}");
    }

    private const string Both = "0:S[2,1]P[]R1;1:S[3]P[0]R1;2:S[3]P[0]R1;3:S[]P[1,2]R1";
    private const string First = "0:S[1]P[]R1;1:S[3]P[0]R1;2:S[3]P[]R0;3:S[]P[1,2]R1";
    private const string Second = "0:S[2]P[]R1;1:S[3]P[]R0;2:S[3]P[0]R1;3:S[]P[1,2]R1";
    private const string NoEdges = "0:S[]P[]R1;1:S[3]P[]R0;2:S[3]P[]R0;3:S[]P[1,2]R0";
    private const string Duplicate = "0:S[1,1]P[]R1;1:S[3]P[0,0]R1;2:S[3]P[]R0;3:S[]P[1,2]R1";

    [Theory]
    [InlineData("true-then-branch")]
    [InlineData("false-then-branch")]
    [InlineData("unconditional")]
    [InlineData("repeated")]
    [InlineData("retargeted")]
    [InlineData("coincident-terminal")]
    [InlineData("lone-conditional")]
    [InlineData("empty")]
    [InlineData("nonbranch")]
    public void Edges(string id)
    {
        switch (id)
        {
            case "true-then-branch":
            case "false-then-branch":
                bool truth = id == "true-then-branch";
                Check(Setup(new Instruction(Opcode.LoadConst_Bool, truth ? 1 : 0),
                    new Instruction(truth ? Opcode.BranchTrue : Opcode.BranchFalse, 2),
                    new Instruction(Opcode.Branch, 1)), Both);
                break;
            case "unconditional":
                Check(Setup(new Instruction(Opcode.Branch, 1)), First);
                break;
            case "repeated":
            case "retargeted":
                var f = Setup(new Instruction(Opcode.Branch, 1));
                Check(f, First);
                if (id == "retargeted") f.Blocks[0].Instructions[0] = new Instruction(Opcode.Branch, 2);
                Check(f, id == "retargeted" ? Second : First);
                break;
            case "coincident-terminal":
                Check(Setup(new Instruction(Opcode.Nop), new Instruction(Opcode.BranchTrue, 1)), Duplicate);
                break;
            case "lone-conditional":
                Check(Setup(new Instruction(Opcode.BranchTrue, 2)), Second);
                break;
            case "empty":
                Check(Setup(), NoEdges);
                break;
            case "nonbranch":
                Check(Setup(new Instruction(Opcode.Nop)), NoEdges);
                break;
            default: throw new System.ArgumentException(id);
        }
    }
}
