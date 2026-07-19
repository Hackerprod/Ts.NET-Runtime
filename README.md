# TS.NET Runtime (TypeSharp)

An embeddable .NET runtime for real TypeScript-oriented code. The goal is to let TypeScript developers write familiar code without learning runtime-specific primitive types or a custom scripting language.

## What is this?

TypeSharp executes `.ts` source inside .NET through its own parser, binder, IR, bytecode VM, and host interop layer. The public programming model should feel close to a constrained Node-like TypeScript environment: `number`, `boolean`, `bigint`, `string`, arrays, objects, classes, `Uint8Array`, imports, and explicit host modules.

Internal VM types may preserve .NET precision, but developers should not need to write `int32`, `uint64`, `float64`, or `bytes` in normal TypeScript code.

```
TypeScript source (.ts files)
        |
  Custom C# Parser
        |
  Type System & Binder
        |
  Typed IR (Intermediate Representation)
        |
  Bytecode Compiler
        |
  VM Interpreter / .NET JIT
        |
  Native Execution
```

## Feature Status

### Implemented

- Custom lexer/parser for TypeScript syntax (classes, functions, control flow, literals)
- TypeScript-facing primitive surface: `number`, `boolean`, `bigint`, `string`, `void`, `null`, `undefined`
- Byte interop through `Uint8Array` / `ArrayBuffer` shape instead of base64 strings
- Typed IR (Intermediate Representation) with 105 opcodes
- Bytecode compiler with branch patching and constant pooling
- VM interpreter with typed stack, call frames, and instruction limit enforcement
- Class support: constructors, fields, methods, `this` reference
- Object literals and property access
- String concatenation, numeric arithmetic, boolean logic
- Internal exact numeric lanes for .NET interop, including 64-bit integer preservation
- BigInt literal support with TypeScript `123n` syntax
- Throw/catch statement support
- Type checking: no implicit coercion (`"hello" + 5` is a compile error)
- Argument count/type validation at bind time
- Boolean condition warnings in `if`/`while`
- Host interop: `[TsExport]` attribute for selective method export
- Host interop: `ExportMode` (ExplicitOnly, Public, All)
- Host interop: `Task<T>`/`ValueTask<T>` async support
- Host interop: decimal, ulong as TypeScript `bigint`, DTO object marshalling, `byte[]` as `Uint8Array`
- Per-execution allocation budget (logical tracking, .NET GC handles physical memory)
- Hot reload: generational module reloading with type compatibility checks
- Module system with canonical file-based naming
- Runtime limits: instruction count, memory budget, recursion depth, timeout
- Multi-targeting: `net8.0` + `net9.0`
- 119 passing tests (syntax, VM, integration)

### Experimental

- Hot reload with instance state migration
- `TypeSharpProxy` for host function invocation from TS code
- `DynamicHostProxy` for interface-based host function access
- Permission system (TOML-based per-module permissions)

### Planned

- JIT compilation (expression trees -> native code)
- Reified generics (`Repository<User>` runtime type info)
- Nullability (`User?`) with explicit checks
- Cross-module imports with type checking
- Interface implementation validation
- Virtual method dispatch
- Generics with type constraints
- `for...of` / `for...in` loops
- Destructuring assignment
- Spread operator
- Async/await syntax in TS source
- NuGet packages for public consumption
- VS Code extension
- Benchmarks suite

### Not Supported

- Full JavaScript/TypeScript standard library compatibility
- Prototype chains
- Implicit type coercion
- Runtime eval()
- CommonJS/ESM module loading from npm
- Browser execution

## TypeScript Surface

Write normal TypeScript-facing annotations:

| TypeScript | Host/.NET boundary |
|---|---|
| `number` | `double` by default; host APIs may marshal narrower numeric types internally |
| `boolean` | `bool` |
| `bigint` | `long` / `ulong` |
| `string` | `string` |
| `Uint8Array` / `ArrayBuffer` | `byte[]` |
| object/interface shapes | DTOs / public object properties |
| `Promise<T>` | `Task<T>` / `ValueTask<T>` |

Legacy internal aliases may still exist while the VM evolves, but they are not the intended developer-facing API.

## Quick Start

```ts
export function add(a: number, b: number): number {
    return a + b;
}

export function factorial(n: number): number {
    if (n <= 1) {
        return 1;
    }
    return n * factorial(n - 1);
}

export class UserService {
    private users: Map<number, User>;

    constructor() {
        this.users = new Map<number, User>();
    }

    add(user: User): void {
        this.users.set(user.id, user);
    }

    find(id: number): User | null {
        return this.users.get(id);
    }
}
```

## Usage from C#

```csharp
await using var runtime = new TypeSharpRuntimeBuilder()
    .AddSourceDirectory("./scripts")
    .EnableHotReload()
    .ConfigureLimits(options =>
    {
        options.ExecutionTimeout = TimeSpan.FromSeconds(1);
        options.MaximumInstructions = 1_000_000;
        options.MaximumMemoryBytes = 64 * 1024 * 1024;
    })
    .AddHostService("logger", new LoggerApi())
    .BuildAsync();

var module = await runtime.ImportAsync("main.ts");

int result = await module.InvokeAsync<int>(
    "calculate",
    20,
    30
);
```

## Architecture

```
TypeSharp/
  TypeSharp.Syntax/       Lexer, Parser, SyntaxTree
  TypeSharp.Semantics/    TypeSystem, Binder, Symbols, Diagnostics
  TypeSharp.IR/           Instructions, ControlFlow, Optimizations
  TypeSharp.VM/           Bytecode, Interpreter, Memory
  TypeSharp.Runtime/      Objects, Collections, Modules, Generations
  TypeSharp.Interop/      HostExports, Marshalling, Proxies
  TypeSharp.Hosting/      RuntimeBuilder, HotReload, CLI
```

## Hot Reload

When enabled, the runtime watches source directories for changes:

```
Generation 20: active modules, types, bytecode, live instances
Generation 21: new code, type-compatible changes, atomic swap
```

Modules with compatible type changes can migrate instances automatically:
- Adding a field with a default value: compatible
- Removing a field: incompatible (requires migrator)
- Changing field type: incompatible

## Security

Each module can have restricted permissions:

```toml
[permissions]
filesystem = false
network = false
clock = true
host.database = true
host.logger = true
```

Runtime limits:
- Maximum instructions per execution
- Maximum memory allocation
- Maximum recursion depth
- Execution timeout with cooperative cancellation

## Building

```bash
dotnet build
dotnet test
```

## License

MIT
