export function int64Compare(a: int64, b: int64): int32 {
    let code: int32 = 0;
    if (a < b) { code = code + 1; }
    if (a <= b) { code = code + 2; }
    if (a == b) { code = code + 4; }
    if (a != b) { code = code + 8; }
    if (a > b) { code = code + 16; }
    if (a >= b) { code = code + 32; }
    return code;
}

export function uint64Compare(a: uint64, b: uint64): int32 {
    let code: int32 = 0;
    if (a < b) { code = code + 1; }
    if (a <= b) { code = code + 2; }
    if (a == b) { code = code + 4; }
    if (a != b) { code = code + 8; }
    if (a > b) { code = code + 16; }
    if (a >= b) { code = code + 32; }
    return code;
}

export function uint64Pipeline(a: uint64, b: uint64): uint64 {
    const sum: uint64 = a + b;
    const product: uint64 = sum * 3;
    const reduced: uint64 = product - b;
    return reduced / 2;
}

export function uint64Remainder(a: uint64, b: uint64): uint64 {
    return a % b;
}

export function int64Bitwise(a: int64, b: int64): int64 {
    const mixed: int64 = (a ^ b) | (a << 4);
    return mixed & 9223372036854775807;
}

export function float32Pipeline(a: float32, b: float32): float32 {
    return (a * b + a) / b;
}

export function decimalPipeline(a: decimal, b: decimal, c: decimal): decimal {
    return (a + b) * c - a / c;
}

export function decimalCompare(a: decimal, b: decimal): int32 {
    if (a < b) { return -1; }
    if (a > b) { return 1; }
    return 0;
}

export function signedRemainder(a: int32, b: int32): int32 {
    return a % b;
}

export function divideByZero(value: int32): int32 {
    return value / 0;
}
