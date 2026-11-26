using System.Linq;
using TypeSharp.IR;
using TypeSharp.IR.Optimizations;
using TypeSharp.Semantics.TypeSystem;
using Xunit;
using static TypeSharp.IR.Opcode;

namespace TypeSharp.Tests;

public sealed class DoubleNegationOptimizationTests
{
    [Theory]
    [InlineData("any-argument", LoadArg)]
    [InlineData("i32-zero", LoadConst_I32)]
    public void DoubleNegationPreservesBooleanCoercion(string id, Opcode load)
    {
        var function = new FunctionIR(id, TsType.Bool,
            load == LoadArg ? [new ParameterInfo("value", TsType.Any)] : []);
        Check(function, [(load, 0), (Not_Bool, 0), (Not_Bool, 0), (Return, 0)],
            [(load, 0), (Not_Bool, 0), (Not_Bool, 0), (Return, 0)]);
    }

    [Fact]
    public void SingleBangIsPreserved()
    {
        var function = new FunctionIR("single-bang", TsType.Bool, [new ParameterInfo("value", TsType.Any)]);
        Check(function, [(LoadArg, 0), (Not_Bool, 0), (Return, 0)],
            [(LoadArg, 0), (Not_Bool, 0), (Return, 0)]);
    }

    [Fact]
    public void NopThenConstantsAreOptimizedToFixedPoint()
    {
        var function = new FunctionIR("nop-fold", TsType.Int32, []);
        Check(function, [(LoadConst_I32, 2), (Nop, 0), (LoadConst_I32, 3), (Add_I32, 0), (Return, 0)],
            [(LoadConst_I32, 5), (Return, 0)]);
    }

    [Fact]
    public void RedundantLocalIdentityIsRemoved()
    {
        var function = new FunctionIR("local-identity", TsType.Int32, []) { LocalCount = 1 };
        Check(function, [(LoadConst_I32, 4), (StoreLocal, 0), (LoadLocal, 0), (StoreLocal, 0), (LoadLocal, 0), (Return, 0)],
            [(LoadConst_I32, 4), (StoreLocal, 0), (LoadLocal, 0), (Return, 0)]);
    }

    private static void Check(FunctionIR function, (Opcode, int)[] input, (Opcode, int)[] expected)
    {
        var block = function.CreateBlock();
        foreach (var (opcode, operand) in input)
            block.Instructions.Add(new Instruction(opcode, operand));

        new IRPipeline().Optimize(function);

        var optimized = Assert.Single(function.Blocks);
        Assert.All(optimized.Instructions, instruction =>
        {
            Assert.Equal(0, instruction.Operand1);
            Assert.Null(instruction.OperandObject);
        });
        var actual = optimized.Instructions.Select(instruction => (instruction.Opcode, instruction.Operand0)).ToArray();
        Assert.True(expected.SequenceEqual(actual),
            $"{function.Name}: expected {Describe(expected)}; actual {Describe(actual)}");
    }

    private static string Describe((Opcode, int)[] sequence) =>
        string.Join(" ", sequence.Select(x => $"{x.Item1}({x.Item2})"));
}
