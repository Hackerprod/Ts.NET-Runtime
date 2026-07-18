namespace TypeSharp.IR.ControlFlow;

public sealed class ControlFlowGraph
{
    public List<BasicBlock> Blocks { get; }
    public BasicBlock Entry { get; }
    public BasicBlock Exit { get; }

    public ControlFlowGraph(FunctionIR function)
    {
        Blocks = new List<BasicBlock>(function.Blocks);
        Entry = function.Blocks.Count > 0 ? function.Blocks[0] : new BasicBlock(0);
        Exit = new BasicBlock(function.Blocks.Count);
        Blocks.Add(Exit);

        BuildEdges();
    }

    private void BuildEdges()
    {
        foreach (var block in Blocks)
        {
            if (block.Instructions.Count == 0) continue;

            var last = block.LastInstruction;
            switch (last.Opcode)
            {
                case Opcode.Branch:
                    block.Successors.Add(last.Operand0);
                    break;

                case Opcode.BranchTrue:
                case Opcode.BranchFalse:
                    block.Successors.Add(last.Operand0);
                    if (block.Instructions.Count > 1)
                    {
                        int nextBlockIdx = Blocks.IndexOf(block) + 1;
                        if (nextBlockIdx < Blocks.Count)
                            block.Successors.Add(Blocks[nextBlockIdx].Id);
                    }
                    break;

                case Opcode.Return:
                case Opcode.ReturnVoid:
                case Opcode.Throw:
                    block.Successors.Add(Exit.Id);
                    break;
            }
        }

        foreach (var block in Blocks)
        {
            foreach (var succId in block.Successors)
            {
                var succ = Blocks.FirstOrDefault(b => b.Id == succId);
                if (succ != null)
                    succ.Predecessors.Add(block.Id);
            }
        }
    }

    public bool IsReachable(BasicBlock block)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(Entry.Id);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (id == block.Id) return true;
            if (!visited.Add(id)) continue;

            var b = Blocks.FirstOrDefault(bl => bl.Id == id);
            if (b != null)
            {
                foreach (var succ in b.Successors)
                    queue.Enqueue(succ);
            }
        }

        return false;
    }
}

public sealed class ControlFlowAnalyzer
{
    public static bool Analyze(FunctionIR function)
    {
        if (function.Blocks.Count == 0) return true;

        var cfg = new ControlFlowGraph(function);

        foreach (var block in function.Blocks)
        {
            if (!cfg.IsReachable(block) && block != cfg.Entry)
                continue;

            if (block.Instructions.Count == 0 && block.Successors.Count == 0 &&
                block != cfg.Exit)
            {
                return false;
            }
        }

        return true;
    }
}
