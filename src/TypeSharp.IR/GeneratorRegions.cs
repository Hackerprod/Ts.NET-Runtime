using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;

namespace TypeSharp.IR;

public sealed partial class IRGenerator
{
    // Only represented statements are walked. Bodies of lifted declarations,
    // methods and function expressions belong to independent activations.
    private static bool RequiresGeneratorRegions(BoundNode statement)
    {
        var breaks = new Stack<int>();
        var continues = new Stack<int>();
        var labels = new Dictionary<string, (int Depth, bool Iteration)>(StringComparer.Ordinal);

        static bool Iteration(BoundNode node) => node switch
        {
            BoundForStatement or BoundWhileStatement or BoundDoWhileStatement => true,
            BoundLabelledStatement label => Iteration(label.Statement),
            _ => false
        };

        bool Loop(BoundNode body, int depth)
        {
            breaks.Push(depth);
            continues.Push(depth);
            try { return Visit(body, depth); }
            finally { continues.Pop(); breaks.Pop(); }
        }

        bool Visit(BoundNode node, int depth)
        {
            switch (node)
            {
                case BoundTryStatement region:
                    return region.FinallyBlock is not null || Visit(region.TryBlock, depth + 1)
                        || (region.CatchBlock is not null && Visit(region.CatchBlock, depth + 1));
                case BoundBlockStatement block:
                    return block.Statements.Any(s => Visit(s, depth));
                case BoundIfStatement branch:
                    return Visit(branch.ThenBranch, depth) || (branch.ElseBranch is not null && Visit(branch.ElseBranch, depth));
                case BoundForStatement loop: return Loop(loop.Body, depth);
                case BoundWhileStatement loop: return Loop(loop.Body, depth);
                case BoundDoWhileStatement loop: return Loop(loop.Body, depth);
                case BoundSwitchStatement branch:
                    breaks.Push(depth);
                    try { return branch.Clauses.Any(c => c.Statements.Any(s => Visit(s, depth))); }
                    finally { breaks.Pop(); }
                case BoundLabelledStatement label:
                    labels.Add(label.Label, (depth, Iteration(label.Statement)));
                    try { return Visit(label.Statement, depth); }
                    finally { labels.Remove(label.Label); }
                case BoundBreakStatement leave:
                    return leave.Label is { } breakLabel
                        ? labels.TryGetValue(breakLabel, out var breakTarget) && depth > breakTarget.Depth
                        : breaks.TryPeek(out int b) && depth > b;
                case BoundContinueStatement repeat:
                    return repeat.Label is { } continueLabel
                        ? labels.TryGetValue(continueLabel, out var continueTarget) && continueTarget.Iteration && depth > continueTarget.Depth
                        : continues.TryPeek(out int c) && depth > c;
                default:
                    return false;
            }
        }

        return Visit(statement, 0);
    }

    private void GenerateGeneratorTry(BoundTryStatement statement, FunctionIR function)
    {
        var parent = function.GeneratorOwner;
        var enter = function.CreateBlock();
        var after = function.CreateBlock();
        // The normal successor must be the block immediately after GEnter.
        // Move the after-block behind that successor without changing IDs.
        function.GeneratorOwner = new(enter.Id, GeneratorBlockPhase.Try, parent);
        var body = function.CreateBlock();

        BasicBlock? handler = null;
        if (statement.CatchBlock is not null)
        {
            function.GeneratorOwner = new(enter.Id, GeneratorBlockPhase.Catch, parent);
            handler = function.CreateBlock();
        }
        BasicBlock? cleanup = null;
        if (statement.FinallyBlock is not null)
        {
            function.GeneratorOwner = new(enter.Id, GeneratorBlockPhase.Finally, parent);
            cleanup = function.CreateBlock();
        }

        EmitBranch(enter.Id);
        enter.Instructions.Add(new(Opcode.GEnter, handler?.Id ?? -1, cleanup?.Id ?? -1, body.Id));
        function.GeneratorOwner = body.GeneratorOwner;
        _currentBlock = body;
        GenerateStatement(statement.TryBlock);
        if (!_currentBlock.EndsInBranch) EmitBranch(after.Id);

        if (handler is not null)
        {
            function.GeneratorOwner = handler.GeneratorOwner;
            _currentBlock = handler;
            if (statement.CatchVariable is LocalSymbol local)
                EmitStoreLocal(GetLocalIndex(local));
            else
                _currentBlock.Instructions.Add(new(Opcode.Pop));
            GenerateStatement(statement.CatchBlock!);
            if (!_currentBlock.EndsInBranch) EmitBranch(after.Id);
        }

        if (cleanup is not null)
        {
            function.GeneratorOwner = cleanup.GeneratorOwner;
            _currentBlock = cleanup;
            GenerateStatement(statement.FinallyBlock!);
            if (!_currentBlock.EndsInBranch)
                _currentBlock.Instructions.Add(new(Opcode.GEndFinally));
        }

        function.GeneratorOwner = parent;
        _currentBlock = after;
    }

    private static void FinishGeneratorRegions(FunctionIR function)
    {
        var blocks = function.Blocks;
        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.TryPop(out int id))
        {
            if (!reachable.Add(id)) continue;
            foreach (var ins in blocks[id].Instructions)
            {
                switch (ins.Opcode)
                {
                    case Opcode.Branch: case Opcode.BranchTrue: case Opcode.BranchFalse: case Opcode.GTransfer:
                        pending.Push(ins.Operand0);
                        break;
                    case Opcode.GEnter:
                        if (ins.Operand0 >= 0) pending.Push(ins.Operand0);
                        if (ins.Operand1 >= 0) pending.Push(ins.Operand1);
                        pending.Push((int)ins.OperandObject!);
                        break;
                }
            }
        }

        // Keep the prescribed adjacency of every GEnter and its normal Try
        // successor. Apart from that, the prior source-block order is retained.
        var primarySuccessors = blocks.Where(b => b.Instructions.LastOrDefault()?.Opcode == Opcode.GEnter)
            .ToDictionary(b => b.Id, b => (int)b.LastInstruction.OperandObject!);
        var moved = primarySuccessors.Values.ToHashSet();
        var ordered = new List<BasicBlock>(reachable.Count);
        foreach (var block in blocks)
        {
            if (!reachable.Contains(block.Id) || moved.Contains(block.Id)) continue;
            ordered.Add(block);
            if (primarySuccessors.TryGetValue(block.Id, out var body))
                ordered.Add(blocks[body]);
        }

        blocks.Clear();
        blocks.AddRange(ordered);
        foreach (var block in blocks)
        {
            var owner = block.GeneratorOwner;
            block.Instructions.Insert(0, new(Opcode.GBlock, owner?.EnterBlock ?? -1, (int)(owner?.Phase ?? GeneratorBlockPhase.Outside)));
        }
        blocks[0].Instructions.Insert(0, new(Opcode.GStart));
    }
}
