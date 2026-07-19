// Baseline TypeSharp declarations for applications executed by the VM.
//
// Keep this file application-agnostic. Put project-specific capabilities in
// `types/host.d.ts` through module augmentation of `@runtime/host`.

declare type RuntimeJsonPrimitive = string | number | boolean | bigint | null;
declare type RuntimeJsonValue =
    | RuntimeJsonPrimitive
    | Uint8Array
    | readonly RuntimeJsonValue[]
    | { readonly [key: string]: RuntimeJsonValue };

declare module "@runtime/host" {
    export type HostPrimitive = string | number | boolean | bigint | null;
    export type HostValue =
        HostPrimitive | Uint8Array | readonly HostValue[] | { readonly [key: string]: HostValue };

    export interface HostCallOptions {
        readonly timeoutMs?: number;
    }

    export interface Capabilities {}

    export type CapabilityName = keyof Capabilities & string;

    export function has(name: string): boolean;
    export function capability<TName extends CapabilityName>(name: TName): Capabilities[TName];
    export function capability<TApi extends object = Record<string, unknown>>(name: string): TApi;
    export function call<TResult = unknown>(
        capabilityName: string,
        operationName: string,
        args?: readonly HostValue[],
        options?: HostCallOptions
    ): TResult;
}
