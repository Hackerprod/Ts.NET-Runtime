# TypeSharp Standard TS Template

The starter in `src/main.ts` runs in the TypeSharp runtime without imports, host
services, or Node. Its exported `main` function returns `42`.

## Run from a .NET host

Pass the absolute path to this template's `src` directory, not the template root,
to the runtime builder. For example, from the repository root:

```csharp
using System;
using System.IO;
using TypeSharp.Hosting;

var src = Path.GetFullPath("templates/standard-ts/src");
await using var runtime = await new TypeSharpRuntimeBuilder()
    .AddSourceDirectory(src)
    .BuildAsync();
var result = await runtime.InvokeAsync<double>("src", "main");
Console.WriteLine(result); // 42
```

When the source root contains the entry file `main.ts`, its directory name is
the module ID. Here the source root is named `src`, so the module ID is `src`.

## Optional Node tooling

Use these commands from the template root for type-checking, linting, and
formatting. The TypeSharp runtime executes the `.ts` source directly and does
not load Node, npm packages, or emitted JavaScript.

```powershell
npm install
npm run typecheck
npm run lint
npm run format
```

## Host Declarations

The `.d.ts` files under `types/` are for editors and `tsc`; the runtime loader
ignores them. You can keep project-specific types in `types/host.d.ts` by
augmenting `@runtime/host`, and keep runtime-wide declarations in
`types/typesharp-runtime.d.ts`. These declarations neither register nor expose a
host service and do not make the bare `@runtime/host` import executable. Using
actual host service exports requires separate host registration and a supported
invocation path. The starter does not need any host services.
