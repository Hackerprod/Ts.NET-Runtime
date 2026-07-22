// TypeSharp runtime declarations.
//
// This file is intentionally self-contained and is used with `noLib: true`.
// Do not add `lib: ["ES2020"]`: every declared API here must correspond to
// the runtime capability manifest and a VM/binder implementation.

interface Object {}
interface Function {}
interface CallableFunction extends Function {}
interface NewableFunction extends Function {}
interface IArguments extends ArrayLike<unknown> {}
interface String {}
interface Boolean {}
interface Number {}
interface RegExp {}

interface ArrayLike<T> {
    readonly length: number;
    readonly [n: number]: T;
}

type Record<K extends string | number | symbol, T> = {
    [P in K]: T;
};

interface Array<T> {
    length: number;
    [n: number]: T;
    push(...items: T[]): number;
    pop(): T | undefined;
    shift(): T | undefined;
    unshift(...items: T[]): number;
    slice(start?: number, end?: number): T[];
    indexOf(searchElement: T, fromIndex?: number): number;
    includes(searchElement: T, fromIndex?: number): boolean;
    join(separator?: string): string;
    reverse(): T[];
    concat(...items: T[][]): T[];
    map<U>(callback: (value: T, index: number, array: T[]) => U): U[];
    filter(callback: (value: T, index: number, array: T[]) => boolean): T[];
    forEach(callback: (value: T, index: number, array: T[]) => void): void;
    reduce<U>(callback: (accumulator: U, value: T, index: number, array: T[]) => U, initialValue: U): U;
    some(callback: (value: T, index: number, array: T[]) => boolean): boolean;
    every(callback: (value: T, index: number, array: T[]) => boolean): boolean;
    find(callback: (value: T, index: number, array: T[]) => boolean): T | undefined;
    findIndex(callback: (value: T, index: number, array: T[]) => boolean): number;
    sort(compareFn?: (a: T, b: T) => number): T[];
    flatMap<U>(callback: (value: T, index: number, array: T[]) => U | U[]): U[];
}

interface ReadonlyArray<T> extends ArrayLike<T> {
    readonly [n: number]: T;
    slice(start?: number, end?: number): T[];
    indexOf(searchElement: T, fromIndex?: number): number;
    includes(searchElement: T, fromIndex?: number): boolean;
    join(separator?: string): string;
    map<U>(callback: (value: T, index: number, array: readonly T[]) => U): U[];
    filter(callback: (value: T, index: number, array: readonly T[]) => boolean): T[];
    forEach(callback: (value: T, index: number, array: readonly T[]) => void): void;
    some(callback: (value: T, index: number, array: readonly T[]) => boolean): boolean;
    every(callback: (value: T, index: number, array: readonly T[]) => boolean): boolean;
    find(callback: (value: T, index: number, array: readonly T[]) => boolean): T | undefined;
    findIndex(callback: (value: T, index: number, array: readonly T[]) => boolean): number;
}

interface ArrayConstructor {
    new <T = unknown>(...items: T[]): T[];
    from<T>(source: ArrayLike<T> | readonly T[]): T[];
    from<T, U>(source: ArrayLike<T> | readonly T[], mapFn: (value: T, index: number) => U): U[];
}
declare const Array: ArrayConstructor;

interface String {
    readonly length: number;
    charAt(index: number): string;
    includes(searchString: string, position?: number): boolean;
    startsWith(searchString: string, position?: number): boolean;
    endsWith(searchString: string, endPosition?: number): boolean;
    slice(start?: number, end?: number): string;
    substring(start: number, end?: number): string;
    toLowerCase(): string;
    toUpperCase(): string;
    trim(): string;
    split(separator: string): string[];
    replace(searchValue: string, replaceValue: string): string;
}

interface Math {
    abs(x: number): number;
    floor(x: number): number;
    ceil(x: number): number;
    round(x: number): number;
    trunc(x: number): number;
    sqrt(x: number): number;
    cbrt(x: number): number;
    pow(x: number, y: number): number;
    log(x: number): number;
    log2(x: number): number;
    log10(x: number): number;
    exp(x: number): number;
    sign(x: number): number;
    random(): number;
    min(...values: number[]): number;
    max(...values: number[]): number;
}
declare const Math: Math;

interface NumberConstructor {
    (value?: unknown): number;
    readonly MAX_SAFE_INTEGER: number;
    readonly MIN_SAFE_INTEGER: number;
    isFinite(value: unknown): boolean;
    isNaN(value: unknown): boolean;
    parseInt(value: string, radix?: number): number;
    parseFloat(value: string): number;
}
declare const Number: NumberConstructor;

interface Console {
    log(...values: unknown[]): void;
    warn(...values: unknown[]): void;
    error(...values: unknown[]): void;
}
declare const console: Console;

interface Uint8Array extends ArrayLike<number> {
    readonly length: number;
    slice(start?: number, end?: number): Uint8Array;
    subarray(start?: number, end?: number): Uint8Array;
    set(source: Uint8Array | number[], offset?: number): void;
}
interface Uint8ArrayConstructor {
    new (length: number): Uint8Array;
    new (source: Uint8Array | number[]): Uint8Array;
}
declare const Uint8Array: Uint8ArrayConstructor;

interface Map<K, V> {
    readonly size: number;
    set(key: K, value: V): this;
    get(key: K): V | undefined;
    has(key: K): boolean;
    delete(key: K): boolean;
    clear(): void;
    keys(): K[];
    values(): V[];
    entries(): [K, V][];
    forEach(callback: (value: V, key: K, map: Map<K, V>) => void): void;
}
interface MapConstructor {
    new <K, V>(entries?: readonly (readonly [K, V])[] | null): Map<K, V>;
}
declare const Map: MapConstructor;

interface Set<T> {
    readonly size: number;
    add(value: T): this;
    has(value: T): boolean;
    delete(value: T): boolean;
    clear(): void;
    keys(): T[];
    values(): T[];
    entries(): [T, T][];
    forEach(callback: (value: T, key: T, set: Set<T>) => void): void;
}
interface SetConstructor {
    new <T>(values?: readonly T[] | null): Set<T>;
}
declare const Set: SetConstructor;

interface Date {
    getTime(): number;
    valueOf(): number;
    toISOString(): string;
}
interface DateConstructor {
    new (): Date;
    new (timestamp: number): Date;
    new (value: string): Date;
    new (year: number, month: number, date?: number, hours?: number, minutes?: number, seconds?: number, ms?: number): Date;
}
declare const Date: DateConstructor;

declare function parseInt(value: string, radix?: number): number;
declare function parseFloat(value: string): number;
declare function isNaN(value: unknown): boolean;
declare function isFinite(value: unknown): boolean;
declare function String(value?: unknown): string;
declare function Boolean(value?: unknown): boolean;
declare function BigInt(value: string | number | bigint | boolean): bigint;

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
