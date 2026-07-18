export function recurse(value: int32): int32 {
    return recurse(value + 1);
}

export function safe(): int32 {
    return 42;
}
