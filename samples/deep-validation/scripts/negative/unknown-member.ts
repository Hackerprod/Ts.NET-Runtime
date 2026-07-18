export interface Item {
    value: int32;
}

export function invalid(): int32 {
    const item: Item = { value: 3 };
    return item.missing;
}
