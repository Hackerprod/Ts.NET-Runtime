export interface Box {
    value: int32;
}

export function nullableNull(box: Box?): int32? {
    return box?.value;
}

export function nullableValue(): int32? {
    const box: Box = { value: 8 };
    return box?.value;
}
