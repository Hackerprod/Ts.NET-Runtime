// Generic host-capability declarations for TypeSharp.
//
// The runtime stays application-agnostic. A host can expose concrete APIs by
// augmenting this module in its own .d.ts files:
//
// declare module "@runtime/host" {
//     export interface Capabilities {
//         readonly clock: { now(): number };
//     }
// }

declare module "@runtime/host" {
    export type HostPrimitive = string | number | boolean | bigint | null;
    export type HostValue =
        HostPrimitive |
        Uint8Array |
        readonly HostValue[] |
        { readonly [key: string]: HostValue };

    export interface HostCallOptions {
        readonly timeoutMs?: number;
    }

    export interface Capabilities {
    }

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
