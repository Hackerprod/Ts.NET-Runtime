// TypeSharp standard runtime declarations.
//
// These declarations describe the TypeScript-facing surface that the runtime
// intends to support without exposing internal VM lane names such as int32,
// uint64, float64, or bytes. Host applications should add their own .d.ts
// files for app-specific capabilities.
//
// Use these declarations with a normal TypeScript standard lib such as ES2020;
// standard types like Uint8Array, ArrayBuffer, Math, and console are not
// redeclared here to avoid diverging from TypeScript's official lib files.

declare type RuntimeJsonPrimitive = string | number | boolean | bigint | null;
declare type RuntimeJsonValue =
    RuntimeJsonPrimitive |
    Uint8Array |
    readonly RuntimeJsonValue[] |
    { readonly [key: string]: RuntimeJsonValue };
