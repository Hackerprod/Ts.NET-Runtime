using System.Buffers.Binary;
using TypeSharp.Hosting;
using TypeSharp.Runtime.Generations;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

// Ciclo 01: el linker renombra TODAS las constantes de cadena al
// desambiguar funciones duplicadas. Estos tests construyen el bytecode
// a mano y comprueban que solo los operandos de Call/LoadFunc/
// MakeClosure se re-apuntan al nombre renombrado, y que la tabla
// original y el módulo fuente quedan intactos.
public class LinkerTests
{
    private static byte[] I32(byte opcode, int value)
    {
        var bytes = new byte[5];
        bytes[0] = opcode;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), value);
        return bytes;
    }

    private static byte[] I32x2(byte opcode, int first, int second)
    {
        var bytes = new byte[9];
        bytes[0] = opcode;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), first);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), second);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static BytecodeFunction TrivialFunction(string name) =>
        new(name, new byte[] { Opcodes.ReturnVoid }, 0, 0, false,
            Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>());

    private static BytecodeFunction SaveFunction() =>
        // La cadena "save" se usa en cuatro posiciones que comparten el
        // MISMO índice (0) en la tabla, como hace el compilador al
        // internar por valor exacto.
        new("save",
            Concat(
                I32(Opcodes.LoadConstString, 0), // literal "save" -> sigue en 0
                I32x2(Opcodes.Call, 0, 0),       // llamada a save -> pasa a 2
                I32(Opcodes.LoadField, 0),       // campo "save"   -> sigue en 0
                I32(Opcodes.LoadFunc, 0),        // valor-función  -> pasa a 2
                I32(Opcodes.LoadConstString, 1), // literal "other" -> intacto
                new byte[] { Opcodes.ReturnVoid }),
            0, 0, false,
            new[] { "save", "other" }, Array.Empty<long>(), Array.Empty<double>());

    private static RuntimeGeneration TwoModuleGeneration(
        Func<string, BytecodeFunction[]> functionsByModule) =>
        new(0, new Dictionary<string, TsModule>(StringComparer.Ordinal)
        {
            ["left"] = new TsModule("left", "left.ts",
                new BytecodeModule("left", functionsByModule("left")), 0),
            ["right"] = new TsModule("right", "right.ts",
                new BytecodeModule("right", functionsByModule("right")), 0),
        });

    private static (IReadOnlySet<string> CallReserved, IReadOnlySet<string> ValueReserved) RealReserved(
        IEnumerable<string>? extraHosts = null)
    {
        var valueReserved = new HashSet<string>(Builtins.Names, StringComparer.Ordinal);
        valueReserved.UnionWith(Interpreter.HigherOrderIntrinsicNames);
        var callReserved = new HashSet<string>(valueReserved, StringComparer.Ordinal);
        if (extraHosts != null)
            callReserved.UnionWith(extraHosts);
        return (callReserved, valueReserved);
    }

    private static List<int> ReadStringOperands(BytecodeFunction function)
    {
        var operands = new List<int>();
        var code = function.Instructions;
        var ip = 0;
        while (ip < code.Length)
        {
            var opcode = code[ip];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format),
                $"Unknown opcode 0x{opcode:X2} at {ip}");
            if (opcode is Opcodes.Call or Opcodes.LoadFunc or Opcodes.MakeClosure
                or Opcodes.CallVirt or Opcodes.CallHost or Opcodes.LoadField or Opcodes.HasFieldLiteral
                or Opcodes.StoreField or Opcodes.LoadGlobal or Opcodes.StoreGlobal
                or Opcodes.NewObject or Opcodes.LoadConstString or Opcodes.LoadConstRegex
                or Opcodes.LoadConstBigInt or Opcodes.TypeCheck)
            {
                operands.Add(BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(ip + 1)));
            }

            ip += OpcodeFormats.InstructionSize(opcode);
        }

        return operands;
    }

    [Fact]
    public void LinkedModuleRenamesOnlyFunctionReferenceOperands()
    {
        var generation = TwoModuleGeneration(module => new[]
        {
            SaveFunction(),
            TrivialFunction(module == "left" ? "entryLeft" : "entryRight"),
        });

        // Copias de referencia al módulo fuente: el arreglo no debe tocarlas.
        var sourceLeftSave = generation.Modules["left"].Bytecode.Functions[0];
        var sourceLeftInstructions = sourceLeftSave.Instructions.ToArray();
        var sourceLeftConstants = sourceLeftSave.StringConstants.ToArray();

        var (callReserved, valueReserved) = RealReserved();
        var linked = TypeSharpRuntime.CreateLinkedModule(generation, callReserved, valueReserved);

        // 1. Las funciones se renombran; "save" ya no existe.
        Assert.Contains("$module_left_save", linked.FunctionIndex.Keys);
        Assert.Contains("$module_right_save", linked.FunctionIndex.Keys);
        Assert.DoesNotContain("save", linked.FunctionIndex.Keys);

        // 2. La tabla de la función clonada: "save" intacto en 0,
        // "other" intacto en 1, renombrado añadido en 2.
        var leftSave = linked.Functions[linked.FunctionIndex["$module_left_save"]];
        Assert.Equal(new[] { "save", "other", "$module_left_save" }, leftSave.StringConstants);
        var rightSave = linked.Functions[linked.FunctionIndex["$module_right_save"]];
        Assert.Equal(new[] { "save", "other", "$module_right_save" }, rightSave.StringConstants);

        // 3. Solo los operandos de función se re-apuntan (en orden de
        // aparición: LoadConstString, Call, LoadField, LoadFunc,
        // LoadConstString).
        Assert.Equal(new[] { 0, 2, 0, 2, 1 }, ReadStringOperands(leftSave));
        Assert.Equal(new[] { 0, 2, 0, 2, 1 }, ReadStringOperands(rightSave));

        // 4. El módulo enlazado verifica.
        BytecodeVerifier.Verify(linked);

        // 5. El módulo fuente no se toca.
        Assert.Equal(sourceLeftInstructions, sourceLeftSave.Instructions);
        Assert.Equal(sourceLeftConstants, sourceLeftSave.StringConstants);
        Assert.Equal(new[] { "save", "other" }, sourceLeftSave.StringConstants);

        // 6. Segundo enlazado idempotente.
        var linkedAgain = TypeSharpRuntime.CreateLinkedModule(generation, callReserved, valueReserved);
        Assert.Equal(linked.FunctionIndex.Keys.OrderBy(k => k, StringComparer.Ordinal),
            linkedAgain.FunctionIndex.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var leftSaveAgain = linkedAgain.Functions[linkedAgain.FunctionIndex["$module_left_save"]];
        Assert.Equal(new[] { "save", "other", "$module_left_save" }, leftSaveAgain.StringConstants);
        Assert.Equal(new[] { 0, 2, 0, 2, 1 }, ReadStringOperands(leftSaveAgain));
    }

    [Fact]
    public void LinkedModulePreservesInOperatorKeyAndRenamesFollowingCall()
    {
        var generation = TwoModuleGeneration(module => new[]
        {
            new BytecodeFunction("save",
                Concat(I32(Opcodes.LoadConstI32, module == "left" ? 41 : 42), new[] { Opcodes.Return }),
                0, 0, false, Array.Empty<string>(), Array.Empty<long>(), Array.Empty<double>()),
            new BytecodeFunction(module == "left" ? "entryLeft" : "entryRight",
                Concat(
                    I32(Opcodes.LoadArg, 0),
                    I32(Opcodes.HasFieldLiteral, 0),
                    I32(Opcodes.BranchFalse, 25),
                    I32x2(Opcodes.Call, 0, 0),
                    new[] { Opcodes.Return },
                    I32(Opcodes.LoadConstI32, -1),
                    new[] { Opcodes.Return }),
                1, 0, false, new[] { "save" }, Array.Empty<long>(), Array.Empty<double>()),
        });
        var (callReserved, valueReserved) = RealReserved();
        var linked = TypeSharpRuntime.CreateLinkedModule(generation, callReserved, valueReserved);
        BytecodeVerifier.Verify(linked);

        var interpreter = new Interpreter();
        foreach (var (side, entry, expected) in new[] { ("left", "entryLeft", 41), ("right", "entryRight", 42) })
        {
            var function = linked.Functions[linked.FunctionIndex[entry]];
            Assert.Equal(new[] { "save", $"$module_{side}_save" }, function.StringConstants);
            Assert.Equal(new[] { 0, 1 }, ReadStringOperands(function));

            var obj = new TsObject("Object");
            obj.SetField("save", TsValue.Void);
            Assert.Equal(expected, Assert.IsType<TsInt32Value>(
                interpreter.Execute(linked, entry, new[] { TsValue.FromObject(obj) })).Value);
        }
    }

    [Fact]
    public void LinkedModuleDoesNotRenameBuiltinCallOperands()
    {
        var generation = TwoModuleGeneration(module => new[]
        {
            // Función "parseInt" duplicada en ambos módulos, con un Call
            // a "parseInt" (el builtin) en su propio cuerpo.
            new BytecodeFunction("parseInt",
                Concat(
                    I32x2(Opcodes.Call, 0, 0),
                    new byte[] { Opcodes.ReturnVoid }),
                0, 0, false,
                new[] { "parseInt" }, Array.Empty<long>(), Array.Empty<double>()),
            TrivialFunction(module == "left" ? "entryLeft" : "entryRight"),
        });

        var (callReserved, valueReserved) = RealReserved();
        var linked = TypeSharpRuntime.CreateLinkedModule(generation, callReserved, valueReserved);

        // La definición sí se renombra (está duplicada), pero el operando
        // de Call sigue apuntando a "parseInt", el builtin.
        Assert.Contains("$module_left_parseInt", linked.FunctionIndex.Keys);
        var leftParseInt = linked.Functions[linked.FunctionIndex["$module_left_parseInt"]];
        Assert.Equal(new[] { "parseInt" }, leftParseInt.StringConstants);
        Assert.Equal(new[] { 0 }, ReadStringOperands(leftParseInt));
        BytecodeVerifier.Verify(linked);
    }

    [Fact]
    public void LinkedModuleRenamesLoadFuncOperandsWithHostNames()
    {
        var generation = TwoModuleGeneration(module => new[]
        {
            TrivialFunction("getItem"),
            // Una función de entrada usa "getItem" en dos posiciones que
            // comparten el MISMO índice: un Call y un LoadFunc.
            new BytecodeFunction(module == "left" ? "entryLeft" : "entryRight",
                Concat(
                    I32x2(Opcodes.Call, 0, 0),
                    I32(Opcodes.LoadFunc, 0),
                    new byte[] { Opcodes.ReturnVoid }),
                0, 0, false,
                new[] { "getItem" }, Array.Empty<long>(), Array.Empty<double>()),
        });

        // "getItem" es nombre de host registrado: reservado para Call,
        // pero NO para LoadFunc (en InvokeCallable el módulo se comprueba
        // antes que el host).
        var (callReserved, valueReserved) = RealReserved(new[] { "getItem" });
        var linked = TypeSharpRuntime.CreateLinkedModule(generation, callReserved, valueReserved);

        Assert.Contains("$module_left_getItem", linked.FunctionIndex.Keys);
        var leftEntry = linked.Functions[linked.FunctionIndex["entryLeft"]];
        Assert.Equal(new[] { "getItem", "$module_left_getItem" }, leftEntry.StringConstants);
        // Call sigue en 0 (host); LoadFunc pasa a 1 (función de módulo).
        Assert.Equal(new[] { 0, 1 }, ReadStringOperands(leftEntry));
        BytecodeVerifier.Verify(linked);
    }

    [Fact]
    public void ResolveLinkedEntryPointResolvesDuplicateEntryPointsPerModule()
    {
        // Ciclo 02 §4.4: el resolvedor devuelve, por módulo, el nombre
        // que la función tiene en el módulo enlazado. El helper
        // TwoModuleGeneration construye módulos "left"/"right" (no
        // "modA"/"modB" como esboza el plan); los nombres esperados
        // siguen esa misma convención.
        var generation = TwoModuleGeneration(module => new[]
        {
            TrivialFunction("handle"),
            TrivialFunction(module == "left" ? "onlyLeft" : "onlyRight"),
        });
        var left = generation.Modules["left"];
        var right = generation.Modules["right"];

        // 1. La entrada duplicada se resuelve al nombre renombrado
        // propio de cada módulo.
        Assert.Equal("$module_left_handle",
            TypeSharpRuntime.ResolveLinkedEntryPoint(generation, left, "handle"));
        Assert.Equal("$module_right_handle",
            TypeSharpRuntime.ResolveLinkedEntryPoint(generation, right, "handle"));

        // 2. La función única no se renombra: nombre plano.
        Assert.Equal("onlyLeft",
            TypeSharpRuntime.ResolveLinkedEntryPoint(generation, left, "onlyLeft"));
        Assert.Equal("onlyRight",
            TypeSharpRuntime.ResolveLinkedEntryPoint(generation, right, "onlyRight"));

        // 3. Un módulo que no define la función: error con ámbito de módulo.
        var missing = Assert.Throws<InvalidOperationException>(
            () => TypeSharpRuntime.ResolveLinkedEntryPoint(generation, left, "missing"));
        Assert.Contains("left", missing.Message);
        Assert.Contains("missing", missing.Message);

        // 4. El módulo enlazado contiene los renombrados y ninguna
        // función plana "handle"; verifica sin lanzar.
        var (callReserved, valueReserved) = RealReserved();
        var linked = TypeSharpRuntime.CreateLinkedModule(generation, callReserved, valueReserved);
        Assert.Contains("$module_left_handle", linked.FunctionIndex.Keys);
        Assert.Contains("$module_right_handle", linked.FunctionIndex.Keys);
        Assert.DoesNotContain("handle", linked.FunctionIndex.Keys);
        BytecodeVerifier.Verify(linked);
    }
}
