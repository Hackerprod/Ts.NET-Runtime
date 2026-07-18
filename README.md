# TS.NET Runtime (TypeSharp)

A new programming language with TypeScript syntax and native .NET execution. No JavaScript, no transpilation, no Jint, no V8.

## What is this?

TypeSharp is a statically-typed language that uses TypeScript syntax as its human interface, but executes entirely on .NET through its own parser, type system, IR, bytecode VM, and optional JIT compiler.

```
TypeScript Syntax (.ts files)
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
- Type system with primitive types (`int32`, `uint64`, `float64`, `decimal`, etc.)
- Typed IR (Intermediate Representation) with 105 opcodes
- Bytecode compiler with branch patching and constant pooling
- VM interpreter with typed stack, call frames, and instruction limit enforcement
- Class support: constructors, fields, methods, `this` reference
- Object literals and property access
- String concatenation, numeric arithmetic, boolean logic
- `decimal` type with full arithmetic and comparison opcodes
- `int64`/`uint64` types with branch instruction support
- Throw/catch statement support
- Type checking: no implicit coercion (`"hello" + 5` is a compile error)
- Argument count/type validation at bind time
- Boolean condition warnings in `if`/`while`
- Host interop: `[TsExport]` attribute for selective method export
- Host interop: `ExportMode` (ExplicitOnly, Public, All)
- Host interop: `Task<T>`/`ValueTask<T>` async support
- Host interop: decimal, ulong, DTO object marshalling
- Per-execution allocation budget (logical tracking, .NET GC handles physical memory)
- Hot reload: generational module reloading with type compatibility checks
- Module system with canonical file-based naming
- Runtime limits: instruction count, memory budget, recursion depth, timeout
- Multi-targeting: `net8.0` + `net9.0`
- 82 passing tests (syntax, VM, integration)

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

- JavaScript/TypeScript standard library compatibility
- Dynamic typing / `any` type
- Prototype chains
- `undefined` propagation
- Implicit type coercion
- Runtime eval()
- CommonJS/ESM module loading from npm
- Browser execution

## Primitive Types

```
bool         System.Boolean
int8         System.SByte
uint8        System.Byte
int16        System.Int16
uint16       System.UInt16
int32        System.Int32  (also aliased as `number` where unambiguous)
uint32       System.UInt32
int64        System.Int64
uint64       System.UInt64
float32      System.Single
float64      System.Double
decimal      System.Decimal
string       System.String
bytes        System.Byte[]
datetime     System.DateTimeOffset
guid         System.Guid
```

## Quick Start

```ts
export function add(a: int32, b: int32): int32 {
    return a + b;
}

export function factorial(n: int32): int32 {
    if (n <= 1) {
        return 1;
    }
    return n * factorial(n - 1);
}

export class UserService {
    private users: Map<int32, User>;

    constructor() {
        this.users = new Map<int32, User>();
    }

    add(user: User): void {
        this.users.set(user.id, user);
    }

    find(id: int32): User? {
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
