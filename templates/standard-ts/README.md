# TypeSharp Standard TS Template

This template is for development tooling only. It uses Node-based tools for
editing, linting, formatting, and `tsc --noEmit`; the TypeSharp VM does not load
Node, npm packages, or emitted JavaScript in production.

## Commands

```powershell
npm install
npm run typecheck
npm run lint
npm run format
```

The runtime executes the `.ts` files under `src/`. Declaration files under
`types/` are for editors and `tsc`; the TypeSharp loader ignores `.d.ts` files.

## Host Declarations

Define project-specific host APIs by augmenting `@runtime/host` in
`types/host.d.ts`. Keep runtime-wide declarations in `types/typesharp-runtime.d.ts`
so application capabilities stay separate from the generic runtime surface.
