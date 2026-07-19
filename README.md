# TS.NET Runtime (TypeSharp)

TS.NET Runtime, internally named TypeSharp, is an embeddable .NET runtime for
TypeScript-oriented scripting. It executes `.ts` source through a native C#
pipeline: lexer, parser, binder, typed IR, bytecode compiler, VM interpreter,
and host interop.

The project goal is direct: a TypeScript developer should be able to write
normal TypeScript-style code for an embedded application without learning a
custom scripting language, VM-specific primitive names, or a Node production
runtime.

## Current Status

TypeSharp is under active development and already supports real application
scripting scenarios:

- `.ts` source loading from files and directories
- Strict parser/binder pipeline with diagnostics before execution
- Bytecode VM with instruction, recursion, timeout, and memory limits
- First-class `Promise<T>`, `async`, and `await`
- Host interop for exported .NET services and methods
- `Task<T>` / `ValueTask<T>` exposed to TypeScript as `Promise<T>`
- `Uint8Array` for byte buffers at the TypeScript boundary
- `bigint` for 64-bit integer-facing APIs
- Hot reload with generation tracking and rollback support
- Official `.d.ts` declarations and a copyable TypeScript tooling template
- Multi-targeted packages for `net8.0` and `net9.0`

Validation at the time of this README update:

- `137` passing runtime tests across syntax, VM, and integration suites
- `tsc --noEmit` passes for the packaged runtime declarations
- `tsc --noEmit`, ESLint, and Prettier pass for the standard TS template
- A downstream SKYNET server build and GC TypeScript self-check pass against
  this runtime

## Why This Exists

Many embedded scripting systems force one of two tradeoffs:

- Use JavaScript through a full JS engine and accept a large runtime surface.
- Build a small DSL and make developers learn project-specific syntax.

TypeSharp takes a narrower path. It accepts TypeScript-like source, keeps the
runtime controlled by .NET, and exposes host capabilities explicitly. Node,
npm, ESLint, Prettier, and `tsc` are development tools only; production hosts
execute `.ts` files through TypeSharp.

## Execution Pipeline

```text
TypeScript source (.ts)
        |
        v
Lexer / Parser
        |
        v
Binder / Type System
        |
        v
Typed IR
        |
        v
Bytecode Compiler
        |
        v
VM Interpreter
        |
        v
.NET Host Interop
```

## TypeScript Surface

The public scripting surface is intended to look familiar to TypeScript
developers:

| TypeScript-facing type | .NET / host boundary |
| --- | --- |
| `number` | `double` by default; narrower .NET numeric lanes may be preserved internally |
| `boolean` | `bool` |
| `bigint` | `long` / `ulong` |
| `string` | `string` |
| `Uint8Array` / `ArrayBuffer` | `byte[]` |
| object and interface shapes | DTOs / public object properties |
| `Promise<T>` | `Task<T>` / `ValueTask<T>` |

Internal VM lanes such as `int32`, `uint64`, and `float64` can still exist where
the runtime needs precision, but they are not the preferred developer-facing
API.

## Implemented Features

### Language And Type System

- Functions, classes, constructors, fields, methods, and `this`
- Object literals, arrays, property access, and indexed access
- Numeric, string, boolean, null, undefined, and bigint literals
- `===` and `!==` as first-class strict comparison operators
- `Promise<T>`, `async function`, async methods, async lambdas, and `await`
- Generic syntax and generic function shapes for supported scenarios
- Bind-time argument count and type validation
- Strict primitive behavior with no implicit JavaScript-style coercion
- Throw/catch statement support

### VM And Runtime

- Typed stack and call-frame interpreter
- 105 bytecode opcodes
- Branch patching and constant pooling
- Cooperative execution timeout
- Instruction budget
- Recursion depth limit
- Logical allocation budget
- Canonical file-based module naming
- Hot reload with generation leases and rollback

### Host Interop

- `[TsExport]` for explicit .NET method export
- `ExportMode.ExplicitOnly`, `ExportMode.Public`, and `ExportMode.All`
- Host service registration through `TypeSharpRuntimeBuilder`
- `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` mapped to promises
- DTO-style object marshalling
- `byte[]` mapped to `Uint8Array`
- `long` / `ulong` mapped to `bigint` for TypeScript-facing APIs

### Developer Tooling

- Official declarations in `types/`
- Generic `@runtime/host` capability declarations
- Copyable strict TypeScript template in `templates/standard-ts`
- `package.json`, `tsconfig.json`, `eslint.config.js`, `prettier.config.js`,
  and project-level `.d.ts` examples
- Loader and hot reload ignore `.d.ts` files so declarations are tooling-only

## Not Yet Supported

TypeSharp is not a full JavaScript engine. These are intentionally not treated
as complete today:

- Full JavaScript / TypeScript standard library compatibility
- Node module loading in production
- npm package execution inside the VM
- CommonJS or ESM runtime resolution from `node_modules`
- Browser APIs
- Prototype-chain semantics
- `eval()`
- Complete TypeScript compiler parity

## Roadmap

Planned or experimental areas include:

- Native/JIT execution paths
- Reified generic runtime metadata
- Richer nullability analysis
- Deeper cross-module type checking
- Interface implementation validation
- Virtual method dispatch improvements
- Generic constraints
- `for...of` and `for...in`
- Destructuring assignment
- Spread syntax
- Public NuGet packaging workflow
- VS Code integration
- Benchmarks

## Quick Start

Create a TypeScript script:

```ts
export async function loadProfile(ctx: LoadProfileContext): Promise<boolean> {
    const profile = await ctx.services.profiles.find(ctx.accountId);

    if (profile === null) {
        ctx.reply({ ok: false, accountId: ctx.accountId });
        return false;
    }

    ctx.reply({
        ok: true,
        accountId: profile.accountId,
        displayName: profile.displayName
    });
    return true;
}

interface LoadProfileContext {
    readonly accountId: number;
    readonly services: {
        readonly profiles: {
            find(accountId: number): Promise<Profile | null>;
        };
    };
    reply(payload: RuntimeJsonValue): void;
}

interface Profile {
    readonly accountId: number;
    readonly displayName: string;
}
```

Embed the runtime from C#:

```csharp
await using var runtime = await new TypeSharpRuntimeBuilder()
    .AddSourceDirectory("./scripts")
    .AddHostService("profiles", new ProfileHostService())
    .ConfigureLimits(options =>
    {
        options.ExecutionTimeout = TimeSpan.FromSeconds(1);
        options.MaximumInstructions = 1_000_000;
        options.MaximumMemoryBytes = 64 * 1024 * 1024;
    })
    .EnableHotReload()
    .BuildAsync();

bool handled = await runtime.InvokeAsync<bool>(
    "main",
    "loadProfile",
    context
);
```

Example host service:

```csharp
public sealed class ProfileHostService
{
    [TsExport("find")]
    public Task<object?> FindAsync(double accountId)
    {
        return Task.FromResult<object?>(new
        {
            accountId,
            displayName = "Developer"
        });
    }
}
```

## Runtime Declarations

Runtime-wide declarations live in `types/`:

```ts
/// <reference path="./types/runtime-globals.d.ts" />
/// <reference path="./types/runtime-host.d.ts" />
```

The generic host capability module is declared as `@runtime/host`:

```ts
import { capability } from "@runtime/host";

const clock = capability<{ now(): number }>("clock");
const timestamp = clock.now();
```

Applications should provide their own `.d.ts` files and augment
`@runtime/host` for project-specific services. The runtime package stays
application-agnostic.

## Standard TypeScript Tooling Template

The `templates/standard-ts` folder is designed for application authors who want
normal TypeScript tooling without using Node in production.

It includes:

- `package.json`
- `tsconfig.json`
- `eslint.config.js`
- `prettier.config.js`
- runtime and host `.d.ts` files
- an async `src/main.ts` example

Typical development workflow:

```powershell
cd templates/standard-ts
npm install
npm run typecheck
npm run lint
npm run format
```

Only the source files and declaration files matter to TypeSharp. `node_modules`
and emitted JavaScript are not required by a production host.

## Architecture

```text
src/
  TypeSharp.Syntax/       Lexer, parser, tokens, syntax tree
  TypeSharp.Semantics/    Type system, binder, symbols, diagnostics
  TypeSharp.IR/           Typed intermediate representation
  TypeSharp.VM/           Bytecode, interpreter, memory values
  TypeSharp.Runtime/      Runtime objects, collections, modules, generations
  TypeSharp.Interop/      Host exports, marshalling, proxies
  TypeSharp.Hosting/      Runtime builder, hot reload, CLI host

tests/
  TypeSharp.Syntax.Tests/
  TypeSharp.VM.Tests/
  TypeSharp.Tests/

types/
  Runtime declaration files

templates/
  Copyable TypeScript project templates
```

## Hot Reload

When enabled, TypeSharp watches source directories and publishes new generations
atomically:

```text
Generation 20: active modules, types, bytecode, live handles
Generation 21: validated candidate generation
Generation 21: published after compatibility checks
```

The runtime tracks active execution leases so old generations can remain alive
until in-flight calls finish.

## Security And Limits

Runtime limits are configured by the host:

- Maximum instructions per execution
- Maximum logical memory allocation
- Maximum recursion depth
- Execution timeout with cooperative cancellation

The host controls which services are registered. TypeScript code should access
external capabilities through explicit host APIs, not implicit global access.

## Building And Testing

```powershell
dotnet build TypeSharp.sln -c Debug
dotnet test TypeSharp.sln -c Debug --no-restore
```

Validate runtime declarations:

```powershell
npx --yes --package typescript tsc --noEmit --target ES2020 --module ESNext types/index.d.ts
```

Validate the standard template:

```powershell
cd templates/standard-ts
npm install
npm run typecheck
npm run lint
npm run format
```

## License

MIT
