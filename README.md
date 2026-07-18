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

## Key Differences from TypeScript/JavaScript

- **Real types at runtime**: `int32`, `uint64`, `float64`, `decimal` are real VM types, not just compile-time hints
- **No prototype chains**: Classes have stable structure, field offsets known at compile time
- **No implicit coercions**: `"hello" + 5` is a compile error
- **No `undefined` propagation**: Nullability is explicit (`User?`)
- **Reified generics**: `Repository<User>` knows about `User` at runtime
- **Native .NET interop**: Direct call to C# methods without marshalling through JS
- **Hot reload**: Generational module reloading with state migration
- **Sandboxed execution**: Per-module permissions, instruction limits, memory limits

## Primitive Types

```
bool         System.Boolean
int8         System.SByte
uint8        System.Byte
int16        System.Int16
uint16       System.UInt16
int32        System.Int32
uint32       System.UInt32
int64        System.Int64
uint64       System.UInt64
float32      System.Single
float64      System.Double (also aliased as `number`)
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
  TypeSharp.VM/           Bytecode, Interpreter, Memory, Scheduler
  TypeSharp.Runtime/      Objects, Collections, Modules, Generations
  TypeSharp.Interop/      HostExports, Marshalling, Proxies
  TypeSharp.Jit/          ExpressionTrees, ILBackend (planned)
  TypeSharp.Hosting/      RuntimeBuilder, HotReload, DI
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
