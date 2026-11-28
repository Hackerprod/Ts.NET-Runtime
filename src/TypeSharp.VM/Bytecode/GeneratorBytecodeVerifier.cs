namespace TypeSharp.VM.Bytecode;

public static partial class BytecodeVerifier
{
    private readonly record struct GeneratorInstruction(int Offset, byte Op, int A, int B, int Next);
    private readonly record struct GeneratorScope(int Owner, int Phase);
    private static readonly GeneratorScope OutsideGenerator = new(-1, 0);
    private sealed record GeneratorBlock(int Offset, GeneratorScope Scope, List<GeneratorInstruction> Body);
    private sealed record GeneratorEnter(GeneratorScope Parent, GeneratorInstruction Entry);

    private static IEnumerable<GeneratorInstruction> DecodeGeneratorInstructions(BytecodeFunction function)
    {
        var code = function.Instructions;
        for (int ip = 0; ip < code.Length;)
        {
            int start = ip;
            byte op = code[ip++];
            if (!OpcodeFormats.TryGet(op, out var format) || format.OperandBytes > code.Length - ip)
                Fail(function, $"invalid instruction at {start}");
            int a = format.OperandBytes >= 4 ? ReadInt32(code, ip) : 0;
            int b = format.Operand == OperandKind.Int32x2 ? ReadInt32(code, ip + 4) : 0;
            ip += format.OperandBytes;
            yield return new(start, op, a, b, ip);
        }
    }

    internal static int VerifyStructuredGenerator(BytecodeFunction function)
    {
        if (!function.IsGenerator || function.IsAsync)
            Fail(function, "generator regions require a synchronous generator");

        var blocks = new Dictionary<int, GeneratorBlock>();
        var enters = new Dictionary<int, GeneratorEnter>();
        GeneratorBlock? current = null;
        foreach (var ins in DecodeGeneratorInstructions(function))
        {
            switch (ins.Op)
            {
                case Opcodes.GStart when ins.Offset == 0:
                    break;
                case Opcodes.GBlock:
                    if (ins.B is < 0 or > 3 || (ins.B == 0 ? ins.A != -1 : ins.A < 0))
                        Fail(function, $"invalid region owner/phase at {ins.Offset}");
                    current = new(ins.Offset, new(ins.A, ins.B), []);
                    blocks.Add(current.Offset, current);
                    break;
                case Opcodes.Return: case Opcodes.ReturnVoid:
                case Opcodes.EnterTry: case Opcodes.LeaveTry: case Opcodes.GStart:
                    Fail(function, $"invalid legacy control or repeated marker at {ins.Offset}");
                    break;
                default:
                    if (current is null)
                        throw new BytecodeVerificationException($"Missing generator block in '{function.Name}'");
                    current.Body.Add(ins);
                    if (ins.Op == Opcodes.GEnter)
                        enters.Add(ins.Offset, new(current.Scope, ins));
                    break;
            }
        }

        if (!blocks.TryGetValue(1, out var entry) || entry.Scope != OutsideGenerator)
            Fail(function, "missing outside generator entry block");

        var scopeDepths = new Dictionary<GeneratorScope, int> { [OutsideGenerator] = 0 };
        var roleEntries = new HashSet<int> { 1 };

        GeneratorBlock Target(int offset) => blocks.TryGetValue(offset, out var b)
            ? b : throw new BytecodeVerificationException($"'{function.Name}': invalid generator block target {offset}");

        foreach (var (owner, region) in enters)
        {
            var ins = region.Entry;
            RegisterRole(ins.Next, new(owner, 1));
            if (ins.A < -1 || ins.B < -1 || (ins.A == -1 && ins.B == -1))
                Fail(function, $"missing or invalid handler for generator region at {owner}");
            if (ins.A >= 0) RegisterRole(ins.A, new(owner, 2));
            if (ins.B >= 0) RegisterRole(ins.B, new(owner, 3));
        }

        void RegisterRole(int offset, GeneratorScope scope)
        {
            if (Target(offset).Scope != scope || !roleEntries.Add(offset))
                Fail(function, $"invalid or reused generator role at {offset}");
        }

        int Depth(GeneratorScope scope)
        {
            if (scopeDepths.TryGetValue(scope, out int publicDepth)) return publicDepth;
            var chain = new List<GeneratorScope>();
            var path = new HashSet<GeneratorScope>();
            while (!scopeDepths.TryGetValue(scope, out publicDepth))
            {
                if (!path.Add(scope) || !enters.TryGetValue(scope.Owner, out var currentOwner))
                    throw new BytecodeVerificationException($"'{function.Name}': cyclic or missing generator owner");
                var ins = currentOwner.Entry;
                if ((scope.Phase == 2 && ins.A == -1) || (scope.Phase == 3 && ins.B == -1))
                    Fail(function, "generator block uses an undeclared handler phase");
                chain.Add(scope);
                scope = currentOwner.Parent;
            }
            for (int i = chain.Count - 1; i >= 0; i--)
                scopeDepths.Add(chain[i], ++publicDepth);
            return publicDepth;
        }

        GeneratorBlock OrdinaryTarget(GeneratorBlock from, int offset)
        {
            var dest = Target(offset);
            if (roleEntries.Contains(offset) || dest.Scope != from.Scope)
                Fail(function, $"ordinary branch at block {from.Offset} enters or leaves a generator phase");
            return dest;
        }

        int LastExitedOwner(GeneratorBlock block, GeneratorInstruction ins)
        {
            var dest = Target(ins.A);
            var scope = block.Scope;
            int slots = Depth(scope) - Depth(dest.Scope);
            if (roleEntries.Contains(ins.A) || slots <= 0 || ins.B != slots)
                Fail(function, $"invalid generator exit depth at {ins.Offset}");
            int last = -1;
            for (int i = 0; i < slots; i++)
            {
                last = scope.Owner;
                scope = enters[last].Parent;
            }
            if (scope != dest.Scope)
                Fail(function, $"invalid generator exit phase at {ins.Offset}");
            return last;
        }

        var exits = new Dictionary<int, int>();
        // Check all targets/roles/block ends before the reachability worklist.
        foreach (var block in blocks.Values)
        {
            _ = Depth(block.Scope);
            if (block.Body.Count == 0)
                Fail(function, $"empty generator block at {block.Offset}");
            for (int i = 0; i < block.Body.Count; i++)
            {
                var ins = block.Body[i];
                bool last = i == block.Body.Count - 1;
                switch (ins.Op)
                {
                    case Opcodes.BranchTrue: case Opcodes.BranchFalse:
                        if (i != block.Body.Count - 2 || block.Body[i + 1].Op != Opcodes.Branch)
                            Fail(function, $"conditional has no explicit ordinary fallback at {ins.Offset}");
                        OrdinaryTarget(block, ins.A);
                        break;
                    case Opcodes.Branch:
                        OrdinaryTarget(block, ins.A);
                        if (!last) Fail(function, $"instruction follows branch at {ins.Offset}");
                        break;
                    case Opcodes.GTransfer:
                        exits.Add(ins.Offset, LastExitedOwner(block, ins));
                        if (!last) Fail(function, $"instruction follows region exit at {ins.Offset}");
                        break;
                    case Opcodes.GEndFinally:
                        if (block.Scope.Phase != 3)
                            Fail(function, $"end-finally outside cleanup phase at {ins.Offset}");
                        goto case Opcodes.GEnter;
                    case Opcodes.GEnter: case Opcodes.GReturn: case Opcodes.Throw:
                        if (!last) Fail(function, $"instruction follows generator terminator at {ins.Offset}");
                        break;
                    default:
                        if (last) Fail(function, $"implicit generator block fallthrough at {ins.Offset}");
                        break;
                }
            }
        }

        int peak = 0;
        var depths = new Dictionary<int, int>();
        var baselines = new Dictionary<int, int>();
        var queue = new Queue<GeneratorBlock>();
        Enqueue(1, 0);
        while (queue.TryDequeue(out var block))
        {
            int depth = depths[block.Offset];
            int floor = block.Scope == OutsideGenerator ? 0 : baselines[block.Scope.Owner];
            foreach (var ins in block.Body)
            {
                switch (ins.Op)
                {
                    case Opcodes.GEnter:
                        baselines.Add(ins.Offset, depth);
                        Enqueue(ins.Next, depth);
                        if (ins.A >= 0) Enqueue(ins.A, depth + 1);
                        if (ins.B >= 0) Enqueue(ins.B, depth);
                        break;
                    case Opcodes.GTransfer:
                        Enqueue(ins.A, baselines[exits[ins.Offset]]);
                        break;
                    case Opcodes.GEndFinally:
                        if (depth != floor) Fail(function, $"wrong cleanup exit depth at {ins.Offset}");
                        break;
                    default:
                        depth = ApplyGeneratorStackEffect(function, ins, depth, floor);
                        peak = Math.Max(peak, depth);
                        if (ins.Op is Opcodes.Branch or Opcodes.BranchTrue or Opcodes.BranchFalse)
                            Enqueue(ins.A, depth);
                        break;
                }
            }
        }
        if (depths.Count != blocks.Count)
            Fail(function, "disconnected generator block");
        return peak;

        void Enqueue(int offset, int depth)
        {
            if (depths.TryAdd(offset, depth))
            {
                peak = Math.Max(peak, depth);
                queue.Enqueue(Target(offset));
            }
            else if (depths[offset] != depth)
                Fail(function, $"incompatible generator stack join at {offset}");
        }
    }

    // Ordinary bytecode remains unchanged unless it introduces the dedicated
    // three-input member bridge. Use the ordinary branch and catch depths to
    // validate the actual values consumed by the new instruction there as well.
    private static void VerifyGeneratorMemberStack(BytecodeFunction function)
    {
        var decoded = DecodeGeneratorInstructions(function).ToDictionary(i => i.Offset);
        var depths = new Dictionary<int, int>();
        var work = new Queue<int>();
        Enqueue(0, 0);
        while (work.TryDequeue(out int offset))
        {
            var ins = decoded[offset];
            int depth = ApplyGeneratorStackEffect(function, ins, depths[offset], 0);
            if (ins.Op is Opcodes.Return or Opcodes.ReturnVoid or Opcodes.Throw)
                continue;
            if (ins.Op == Opcodes.EnterTry)
                Enqueue(ins.A, depth + 1);
            if (ins.Op is Opcodes.Branch or Opcodes.BranchTrue or Opcodes.BranchFalse)
                Enqueue(ins.A, depth);
            if (ins.Op != Opcodes.Branch)
                Enqueue(ins.Next, depth);
        }

        void Enqueue(int offset, int depth)
        {
            if (offset == function.Instructions.Length) return; // ordinary code may branch to end
            if (depths.TryAdd(offset, depth))
                work.Enqueue(offset);
            else if (depths[offset] != depth)
                Fail(function, $"incompatible member-reference stack join at {offset}");
        }
    }

    private static int ApplyGeneratorStackEffect(BytecodeFunction f, GeneratorInstruction ins, int depth, int floor)
    {
        var (required, delta) = GeneratorStackEffect(f, ins);
        if (required < 0 || (long)depth - floor < required)
            Fail(f, $"insufficient operands at {ins.Offset}");
        return depth + delta;
    }

    private static int SaturatingPlusOne(int value) => value == int.MaxValue ? int.MaxValue : value + 1;

    private static (int Required, int Delta) GeneratorStackEffect(BytecodeFunction f, GeneratorInstruction ins) =>
        ins.Op switch
        {
            Opcodes.LoadArg or Opcodes.LoadConstBigInt or Opcodes.LoadConstBool or Opcodes.LoadConstDecimal or
            Opcodes.LoadConstF32 or Opcodes.LoadConstF64 or Opcodes.LoadConstI32 or Opcodes.LoadConstI64 or
            Opcodes.LoadConstNull or Opcodes.LoadConstRegex or Opcodes.LoadConstString or Opcodes.LoadConstU64 or
            Opcodes.LoadConstVoid or Opcodes.LoadFunc or Opcodes.LoadGlobal or Opcodes.LoadInvocationReceiver or
            Opcodes.LoadLocal or Opcodes.LoadThis or Opcodes.NewMap => (0, 1),
            Opcodes.ArraySliceFrom or Opcodes.Await or Opcodes.ConvF32F64 or Opcodes.ConvF32I32 or
            Opcodes.ConvF64F32 or Opcodes.ConvF64I32 or Opcodes.ConvF64U64 or Opcodes.ConvI32F32 or
            Opcodes.ConvI32F64 or Opcodes.ConvI32I64 or Opcodes.ConvI32U64 or Opcodes.ConvI64I32 or
            Opcodes.ConvI64U64 or Opcodes.ConvU64F64 or Opcodes.ConvU64I32 or Opcodes.ConvU64I64 or
            Opcodes.DeleteField or Opcodes.EnumerateKeys or Opcodes.HasFieldLiteral or Opcodes.IterableValues or
            Opcodes.LoadField or Opcodes.NegDecimal or Opcodes.NegF32 or Opcodes.NegF64 or
            Opcodes.NegI32 or Opcodes.NegI64 or Opcodes.NewGenerator or Opcodes.NotBool or
            Opcodes.NotI32 or Opcodes.NotI64 or Opcodes.NotU64 or Opcodes.TypeOf => (1, 0),
            Opcodes.AddDecimal or Opcodes.AddF32 or Opcodes.AddF64 or Opcodes.AddI32 or
            Opcodes.AddI64 or Opcodes.AddU64 or Opcodes.AndBool or Opcodes.AndI32 or
            Opcodes.AndI64 or Opcodes.AndU64 or Opcodes.CallDynamicArray or Opcodes.CmpEqAny or
            Opcodes.CmpEqDecimal or Opcodes.CmpEqF32 or Opcodes.CmpEqF64 or Opcodes.CmpEqI32 or
            Opcodes.CmpEqI64 or Opcodes.CmpEqU64 or Opcodes.CmpGeDecimal or Opcodes.CmpGeF32 or
            Opcodes.CmpGeF64 or Opcodes.CmpGeI32 or Opcodes.CmpGeI64 or Opcodes.CmpGeU64 or
            Opcodes.CmpGtDecimal or Opcodes.CmpGtF32 or Opcodes.CmpGtF64 or Opcodes.CmpGtI32 or
            Opcodes.CmpGtI64 or Opcodes.CmpGtU64 or Opcodes.CmpLeDecimal or Opcodes.CmpLeF32 or
            Opcodes.CmpLeF64 or Opcodes.CmpLeI32 or Opcodes.CmpLeI64 or Opcodes.CmpLeU64 or
            Opcodes.CmpLtDecimal or Opcodes.CmpLtF32 or Opcodes.CmpLtF64 or Opcodes.CmpLtI32 or
            Opcodes.CmpLtI64 or Opcodes.CmpLtU64 or Opcodes.CmpNeAny or Opcodes.CmpNeDecimal or
            Opcodes.CmpNeF32 or Opcodes.CmpNeF64 or Opcodes.CmpNeI32 or Opcodes.CmpNeI64 or
            Opcodes.CmpNeU64 or Opcodes.CmpStrictEqAny or Opcodes.CmpStrictNeAny or Opcodes.ConcatString or
            Opcodes.DeleteIndex or Opcodes.DivDecimal or Opcodes.DivF32 or Opcodes.DivF64 or
            Opcodes.DivI32 or Opcodes.DivI64 or Opcodes.DivU64 or Opcodes.LoadElement or
            Opcodes.ModDecimal or Opcodes.ModF64 or Opcodes.ModI32 or Opcodes.ModI64 or
            Opcodes.ModU64 or Opcodes.MulDecimal or Opcodes.MulF32 or Opcodes.MulF64 or
            Opcodes.MulI32 or Opcodes.MulI64 or Opcodes.MulU64 or Opcodes.OrBool or
            Opcodes.OrI32 or Opcodes.OrI64 or Opcodes.OrU64 or Opcodes.PowF64 or
            Opcodes.ShlI32 or Opcodes.ShlI64 or Opcodes.ShlU64 or Opcodes.ShrI32 or
            Opcodes.ShrI64 or Opcodes.ShrU32 or Opcodes.ShrU64 or Opcodes.SubDecimal or
            Opcodes.SubF32 or Opcodes.SubF64 or Opcodes.SubI32 or Opcodes.SubI64 or
            Opcodes.SubU64 or Opcodes.XorI32 or Opcodes.XorI64 or Opcodes.XorU64 => (2, -1),
            Opcodes.Dup or Opcodes.NullCheck or Opcodes.TypeCheck => (1, 1),
            Opcodes.BranchFalse or Opcodes.BranchTrue or Opcodes.GReturn or Opcodes.Pop or
            Opcodes.Return or Opcodes.StoreGlobal or Opcodes.StoreLocal or Opcodes.Throw or
            Opcodes.Yield => (1, -1),
            Opcodes.ArrayAppend or Opcodes.ArrayAppendSpread or Opcodes.CopyObjectFields or Opcodes.StoreField => (2, -2),
            Opcodes.StoreElement => (3, -3),
            Opcodes.GCallMember => (3, -2),
            Opcodes.Call or Opcodes.CallHost or Opcodes.CallVirt or Opcodes.MakeClosure or
            Opcodes.NewObject => (ins.B, 1 - ins.B),
            Opcodes.NewArray => (ins.A, 1 - ins.A),
            Opcodes.CallDynamic or Opcodes.ObjectRest => (SaturatingPlusOne(ins.A), -ins.A),
            Opcodes.Branch or Opcodes.EnterTry or Opcodes.LeaveTry or Opcodes.Nop or
            Opcodes.ReturnVoid => (0, 0),
            _ => throw new BytecodeVerificationException(
                $"'{f.Name}': no generator stack contract for 0x{ins.Op:X2} at {ins.Offset}")
        };
}
