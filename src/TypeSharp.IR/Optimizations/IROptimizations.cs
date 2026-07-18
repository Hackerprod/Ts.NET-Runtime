namespace TypeSharp.IR.Optimizations;

public interface IROptimization
{
    string Name { get; }
    bool Optimize(FunctionIR function);
}

public sealed class ConstantFolding : IROptimization
{
    public string Name => "ConstantFolding";

    public bool Optimize(FunctionIR function)
    {
        bool changed = false;

        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Instructions.Count - 1; i++)
            {
                var curr = block.Instructions[i];
                var next = block.Instructions[i + 1];

                if (curr.Opcode == Opcode.LoadConst_I32 && next.Opcode == Opcode.LoadConst_I32)
                {
                    if (i + 2 < block.Instructions.Count)
                    {
                        var op = block.Instructions[i + 2];
                        if (IsArithmetic(op.Opcode))
                        {
                            int result = op.Opcode switch
                            {
                                Opcode.Add_I32 => curr.Operand0 + next.Operand0,
                                Opcode.Sub_I32 => curr.Operand0 - next.Operand0,
                                Opcode.Mul_I32 => curr.Operand0 * next.Operand0,
                                Opcode.Div_I32 when next.Operand0 != 0 => curr.Operand0 / next.Operand0,
                                Opcode.Mod_I32 when next.Operand0 != 0 => curr.Operand0 % next.Operand0,
                                _ => curr.Operand0
                            };

                            block.Instructions[i] = new Instruction(Opcode.LoadConst_I32, result);
                            block.Instructions.RemoveRange(i + 1, 2);
                            changed = true;
                        }
                    }
                }
            }
        }

        return changed;
    }

    private static bool IsArithmetic(Opcode op) =>
        op is Opcode.Add_I32 or Opcode.Sub_I32 or Opcode.Mul_I32 or Opcode.Div_I32 or Opcode.Mod_I32;
}

public sealed class DeadCodeElimination : IROptimization
{
    public string Name => "DeadCodeElimination";

    public bool Optimize(FunctionIR function)
    {
        bool changed = false;

        foreach (var block in function.Blocks)
        {
            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var inst = block.Instructions[i];

                if (inst.Opcode == Opcode.Nop)
                {
                    block.Instructions.RemoveAt(i);
                    changed = true;
                }
            }
        }

        return changed;
    }
}

public sealed class PeepholeOptimization : IROptimization
{
    public string Name => "PeepholeOptimization";

    public bool Optimize(FunctionIR function)
    {
        bool changed = false;

        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Instructions.Count - 1; i++)
            {
                var curr = block.Instructions[i];
                var next = block.Instructions[i + 1];

                // NOP elimination
                if (curr.Opcode == Opcode.Nop)
                {
                    block.Instructions.RemoveAt(i);
                    changed = true;
                    i--;
                    continue;
                }

                // Load then store same local -> just store
                if (IsLoad(curr.Opcode) && next.Opcode == Opcode.StoreLocal &&
                    curr.Operand0 == next.Operand0 && IsLocalOp(curr.Opcode))
                {
                    block.Instructions.RemoveAt(i);
                    changed = true;
                    i--;
                    continue;
                }

                // Double negation
                if (curr.Opcode == Opcode.Not_Bool && next.Opcode == Opcode.Not_Bool)
                {
                    block.Instructions.RemoveRange(i, 2);
                    changed = true;
                    i--;
                }
            }
        }

        return changed;
    }

    private static bool IsLoad(Opcode op) =>
        op is Opcode.LoadLocal or Opcode.LoadArg;

    private static bool IsLocalOp(Opcode op) =>
        op == Opcode.LoadLocal;
}

public sealed class IRPipeline
{
    private readonly List<IROptimization> _optimizations = new();

    public IRPipeline()
    {
        _optimizations.Add(new ConstantFolding());
        _optimizations.Add(new PeepholeOptimization());
        _optimizations.Add(new DeadCodeElimination());
    }

    public void AddOptimization(IROptimization optimization) => _optimizations.Add(optimization);

    public int Optimize(FunctionIR function, int maxIterations = 10)
    {
        int totalChanges = 0;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool anyChanged = false;

            foreach (var opt in _optimizations)
            {
                if (opt.Optimize(function))
                    anyChanged = true;
            }

            if (!anyChanged) break;
            totalChanges++;
        }

        return totalChanges;
    }
}
