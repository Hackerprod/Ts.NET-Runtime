export function invalid(value: int32): int32 {
    if (value) {
        return 1;
    }
    return 0;
}
