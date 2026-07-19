export function identity<T>(value: T): T {
    return value;
}

class GenericBox {
    pick<T>(value: T): T {
        return value;
    }
}

export function explicitGenericFunction(): number {
    return identity<number>(41) + 1;
}

export function explicitGenericMethod(): string {
    const box = new GenericBox();
    return box.pick<string>("runtime");
}
