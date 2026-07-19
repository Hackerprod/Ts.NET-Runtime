# TS.NET Runtime

TS.NET Runtime is an embeddable TypeScript-oriented runtime for .NET hosts. It
loads `.ts` source files, analyzes them through a native C# compilation pipeline,
compiles them to bytecode, and executes them inside a controlled virtual machine.

The purpose of the project is to let application developers write familiar
TypeScript-style code for embedded workflows without requiring a JavaScript
engine, a production Node.js process, or a project-specific scripting language.

## Design Goals

- Keep the production runtime fully hosted by .NET.
- Make the script authoring experience feel close to normal TypeScript.
- Expose host capabilities explicitly instead of relying on ambient platform
  access.
- Support hot reload without restarting the host process.
- Keep resource limits and host interop under the control of the embedding
  application.
- Use standard TypeScript tooling during development without making Node.js a
  production dependency.

## How It Works

```text
TypeScript source
        |
        v
Lexer and parser
        |
        v
Binder and type analysis
        |
        v
Typed intermediate representation
        |
        v
Bytecode compiler
        |
        v
Virtual machine
        |
        v
.NET host services
```

The host provides source files, declares exported services, configures execution
limits, and invokes script entry points. Script code can call only the
capabilities made available by the host.

## TypeScript Surface

The developer-facing API is intended to use familiar TypeScript types:

| TypeScript type | Host boundary |
| --- | --- |
| `number` | Standard numeric values |
| `boolean` | `bool` |
| `bigint` | 64-bit integer values |
| `string` | `string` |
| `Uint8Array` | `byte[]` |
| `Promise<T>` | `Task<T>` / `ValueTask<T>` |
| Object and interface shapes | DTO-style host values |

Internal VM lanes may use narrower numeric representations when required by the
runtime, but application-facing declarations should prefer idiomatic TypeScript
types.

## Language Model

TS.NET Runtime is not a full JavaScript engine and does not attempt to implement
the complete TypeScript compiler. It implements the language surface needed for
controlled embedded application logic, including:

- Functions, classes, constructors, fields, methods, and `this`.
- Lexical closures and module-level state.
- Object literals, arrays, property access, and indexed access.
- Numeric, string, boolean, null, undefined, and bigint values.
- Strict equality operators `===` and `!==`.
- `Promise`, `async`, and `await` for host-backed asynchronous operations.
- Structured diagnostics before execution.

The runtime intentionally avoids implicit access to Node.js modules, browser
APIs, `eval`, and package execution from `node_modules` inside the VM.

## Host Interop

.NET hosts expose services through explicit registration:

```csharp
await using var runtime = await new TypeSharpRuntimeBuilder()
    .AddSourceDirectory("./scripts")
    .AddHostService("tasks", new TaskService())
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
    "handle",
    request
);
```

Example host service:

```csharp
public sealed class TaskService
{
    [TsExport("find")]
    public Task<object?> FindAsync(double id)
    {
        return Task.FromResult<object?>(new
        {
            id,
            title = "Review request",
            completed = false
        });
    }
}
```

## Script Example

```ts
export async function handle(ctx: RequestContext): Promise<boolean> {
    const task = await ctx.services.tasks.find(ctx.id);

    if (task === null) {
        ctx.reply({ ok: false, reason: "not_found" });
        return false;
    }

    ctx.reply({
        ok: true,
        task
    });

    return true;
}

interface RequestContext {
    readonly id: number;
    readonly services: {
        readonly tasks: {
            find(id: number): Promise<Task | null>;
        };
    };
    reply(payload: unknown): void;
}

interface Task {
    readonly id: number;
    readonly title: string;
    readonly completed: boolean;
}
```

## Declarations

Runtime and host declarations live in `types/`. Applications should provide
their own `.d.ts` files for project-specific services and request shapes.

The generic host capability module is declared as `@runtime/host`:

```ts
import { capability } from "@runtime/host";

const clock = capability<{ now(): number }>("clock");
const timestamp = clock.now();
```

Declaration files are for tooling and type checking. They are not executed by
the VM.

## Tooling Template

The `templates/standard-ts` directory provides a copyable TypeScript authoring
setup:

- `package.json`
- `tsconfig.json`
- `eslint.config.js`
- `prettier.config.js`
- Runtime declaration references
- Example source layout

Typical development workflow:

```powershell
cd templates/standard-ts
npm install
npm run typecheck
npm run lint
npm run format
```

Node.js and npm are development-time tools for type checking, linting, and
formatting. Production execution is handled by the .NET runtime.

## Hot Reload

When hot reload is enabled, source changes are compiled into a new runtime
generation. A validated generation can be published atomically while existing
executions finish on the generation that started them.

```text
Generation N: active code and state
Generation N+1: candidate code compiled and validated
Generation N+1: published after validation
```

This model lets hosts update script behavior without restarting the process while
still isolating in-flight executions from partially loaded changes.

## Security And Limits

The embedding host controls the execution environment:

- Which services are exposed.
- Which source directories are loaded.
- Maximum instruction count.
- Maximum logical memory allocation.
- Maximum recursion depth.
- Execution timeout.

Scripts should communicate with external systems through explicit host services.
The VM does not provide unrestricted filesystem, network, process, or package
access by default.

## Repository Layout

```text
src/
  TypeSharp.Syntax/       Lexer, parser, tokens, syntax tree
  TypeSharp.Semantics/    Binder, symbols, diagnostics, type analysis
  TypeSharp.IR/           Typed intermediate representation
  TypeSharp.VM/           Bytecode, interpreter, runtime values
  TypeSharp.Runtime/      Runtime objects, modules, generations
  TypeSharp.Interop/      Host exports and marshalling
  TypeSharp.Hosting/      Runtime builder, loading, hot reload

tests/
  TypeSharp.Syntax.Tests/
  TypeSharp.VM.Tests/
  TypeSharp.Tests/

types/
  Public TypeScript declarations

templates/
  Copyable TypeScript project templates
```

## Building

```powershell
dotnet build TypeSharp.sln -c Debug
```

Run the test suite:

```powershell
dotnet test TypeSharp.sln -c Debug --no-restore
```

Validate public declarations:

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
