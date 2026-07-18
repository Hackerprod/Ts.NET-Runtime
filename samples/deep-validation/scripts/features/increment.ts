export function increments(value: int32): int32 {
    let current: int32 = value;
    const before: int32 = current++;
    const after: int32 = ++current;
    return before * 100 + after;
}
