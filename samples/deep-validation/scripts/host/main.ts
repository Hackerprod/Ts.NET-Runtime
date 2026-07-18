export function hostInt(value: int32): int32 {
    return hostAdd(value, 7);
}

export function hostInt64(a: int64, b: int64): int64 {
    return hostMix64(a, b);
}

export function hostUInt64(value: uint64): uint64 {
    return hostEchoUInt64(value);
}

export function hostDecimal(a: decimal, b: decimal): decimal {
    return hostDecimalAdd(a, b);
}

export function hostFloat(value: float32): float32 {
    return hostFloatScale(value);
}

export function hostBool(value: bool): bool {
    return hostNegate(value);
}

export function hostString(value: string): string {
    return hostDecorate(value);
}

export function hostTask(value: int32): int32 {
    return hostAsyncDouble(value);
}

export function hostValueTask(value: int64): int64 {
    return hostAsyncIncrement(value);
}

export function hostFailure(value: int32): int32 {
    return hostThrow(value);
}
