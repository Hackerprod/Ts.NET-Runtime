function add(a: int32, b: int32): int32 {
    return a + b;
}

export function invalid(): int32 {
    return add(1);
}
