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
