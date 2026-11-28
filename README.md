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

Serialized bytecode uses TSBC v2 for ordinary modules and v3 for modules
using generator instructions. A module that uses scoped computed object-rest
instructions is written as v4, including when it also uses generator
instructions. v4 needs an updated reader; the current reader still accepts
v2 and v3. Recompile older sources to use the new computed-rest lowering;
loading an older artifact does not add exclusions that were not encoded.

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

In script, `Uint8Array.subarray()` creates a live view of the selected bytes;
`slice()`, `toReversed()`, and construction from an existing `Uint8Array` make
independent copies. `reverse()` mutates only the receiver's visible bytes.

At the host boundary, marshalling a `byte[]` into script or returning a
`Uint8Array` as `byte[]` copies the visible bytes. The low-level
`TsUint8ArrayValue(byte[])` constructor and `TsValue.FromUint8Array(byte[])` instead
wrap the supplied array directly. A root wrapper's `Value` and `RawValue` expose
that live array. A view's getters return a fresh snapshot of only its logical
window, even for full-size or empty views; updating a snapshot does not write
back to the view. The same root/view rule applies when returning the wrapper as
a managed `object`. Standalone `ArrayBuffer` overloads are not implemented.

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

## Run the CLI

Run these commands from the repository checkout root in Bash. Use the .NET 9 SDK
(or a compatible newer SDK) with the required targeting packs
and the .NET 8 runtime. The commands select the `net8.0` target explicitly and use absolute
sample paths expanded by the shell.

### Run the default entry

```bash
dotnet run --project src/TypeSharp.Hosting/TypeSharp.Hosting.csproj --framework net8.0 -- "$PWD/samples/cli/echo.ts"
```

With no function name, the CLI invokes `main` and prints `Result: ok` as the
result line.

### Run a named function with arguments

```bash
dotnet run --project src/TypeSharp.Hosting/TypeSharp.Hosting.csproj --framework net8.0 -- "$PWD/samples/cli/echo.ts" render 007 "two words"
```

The CLI invokes `render` and prints `Result: 007|two words` as the result line.
Tokens after the explicit function name arrive as exact strings,
not parsed as numbers or booleans. Shell quoting makes `two words` one argument.

### Watch the sample directory

```bash
dotnet run --project src/TypeSharp.Hosting/TypeSharp.Hosting.csproj --framework net8.0 -- --watch "$PWD/samples/cli"
```

`--watch` takes exactly one existing directory. It builds and watches executable TypeScript
files in that directory. To see a reload, open `samples/cli/echo.ts`, change
`"ok"` to `"updated"`, and save. It reports successful reloads, for example
`[HotReload] Module 'echo' reloaded (gen N)` (N varies), until you press Ctrl+C.
It does so without calling `main` or `render` or printing their result.

## Host Interop

Register an async .NET host service, then call the script with no arguments:

```csharp
using System.Threading.Tasks;
using TypeSharp.Hosting;
using TypeSharp.Interop.HostExports;

await using var runtime = await new TypeSharpRuntimeBuilder()
    .AddSourceFile("./scripts/main.ts")
    .AddHostService("profiles", new AsyncProfileHost())
    .BuildAsync();

var result = await runtime.InvokeAsync<string>("scripts", "load");
System.Console.WriteLine(result); // profile:42

public class AsyncProfileHost
{
    [TsExport("findProfile")]
    public Task<string> FindProfile(double accountId) => Task.FromResult($"profile:{(int)accountId}");
}
```

The canonical module ID is `scripts` for this single-file setup.
The script can call `findProfile` directly because its name is unique among the
registered host exports. `profiles` is the registration name, not a script object
or an import.

### Canonical module IDs and .NET invocation

With one `AddSourceDirectory("./scripts")`, that directory is the source root.
Canonical module IDs are extensionless paths relative to that root, use slash
separators, and preserve exact case. A nested lowercase `main.ts` uses its
folder ID; a root lowercase `main.ts` uses the root directory basename.
`index.ts` is not special.

| Source path | Canonical module ID |
| --- | --- |
| `scripts/modA.ts` | `modA` |
| `scripts/modB.ts` | `modB` |
| `scripts/billing/main.ts` | `billing` |
| `scripts/billing/index.ts` | `billing/index` |
| `scripts/main.ts` | `scripts` |

`scripts/billing.ts` and `scripts/billing/main.ts` both map to `billing`, so the
same compilation rejects them as duplicate canonical module IDs (TS3001).

If each of `modA.ts` and `modB.ts` defines its own `tick` returning the value
shown, qualified invocation selects the requested module regardless of
registration order:

```csharp
var a = await runtime.InvokeAsync<int>("modA", "tick"); // 10
var b = await runtime.InvokeAsync<int>("modB", "tick"); // 20
```

When names are duplicated, `Invoke("tick")` and an invoked
`CreateFunctionHandle("tick")` can select either module. Module order is
unspecified; do not rely on it.

## Script Example

Save this as `./scripts/main.ts`, relative to the host's working directory:

```ts
async function load(): Promise<string> {
    const profile = await findProfile(42);
    return profile;
}
```

## Declarations

The `declare module "@runtime/host"` block in `types/runtime-host.d.ts` provides
ambient declarations for external TypeScript checking and editor tooling only.
It is not an executable runtime module and is not required for `findProfile`.
Use app-specific `.d.ts` files when external tooling needs to know about your
own host exports.

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

Configure hot reload with `TypeSharpRuntimeBuilder.AddSourceDirectory(...).EnableHotReload().BuildAsync()`.
The enabled builder watches its configured source directory. To request a
reload directly, call `await runtime.ReloadAsync(path, cancellationToken)`;
the token is optional. Watchers debounce changes and dispatch each affected
file individually. The atomic operation is the generation swap, not a batch
deployment.

Reload compiles the requested file and previously loaded sources with registered
host symbols. Compilation errors in those sources can reject the reload, but
an accepted reload publishes only the requested module and carries forward
the other entries. If two loaded files A and B have independently valid
changes and only A reloads successfully (with no concurrent reload), B still
executes its old bytecode until B is separately reloaded, even if the compiler
read B's edited source. In-flight executions keep using the old generation
they already leased. New acquisitions see the published generation after
the swap.

When the initial successful build loaded sources from a single configured
existing source directory, reload compilation uses that original source
directory rather than recomputing it from the loaded paths. With both files
initially loaded from `root`, `root/nested/main.ts` keeps the canonical ID
`nested` and `root/nested/peer.ts` keeps `nested/peer` when main is reloaded.

With subscribers that return normally, direct `ReloadAsync` has these outcomes:

| Condition | Result | Event | Publication |
| --- | --- | --- | --- |
| Disabled, ineligible, or unchanged | `false` | None | No swap |
| Requested-source read retries exhausted with an `IOException` | `false` | `ReloadError` | No swap by this attempt |
| Prepublication rejection (compilation, validation, optional startup or custom canary, or migration) | `false` | `ReloadError` | No swap by this attempt |
| Accepted | `true` | `ModuleReloaded` | Swap and commit source hash before notification |

`ModuleReloaded` carries the canonical module ID and generation ID;
`ReloadError` uses the requested filename stem. Cancellation observed during
the initial source read or retries propagates without a reload event. This
does not cover waiting for the lock or later phases. A throwing `ModuleReloaded` subscriber can
cause `ReloadError` and a `false` result after the swap. A throwing
`ReloadError` subscriber can propagate its exception through `ReloadAsync`. Keep handlers non-throwing, and do not take `ReloadError` alone
as proof that nothing was published. External effects of canaries, migrators,
or host calls are not rolled back.

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
