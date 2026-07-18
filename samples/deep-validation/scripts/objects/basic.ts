export class Accumulator {
    private value: int32;

    constructor(initial: int32) {
        this.value = initial;
    }

    add(amount: int32): int32 {
        this.value = this.value + amount;
        return this.value;
    }

    current(): int32 {
        return this.value;
    }
}

export function instanceIsolation(): int32 {
    const left = new Accumulator(10);
    const right = new Accumulator(100);
    left.add(5);
    right.add(7);
    return left.current() * 1000 + right.current();
}

export function constructorOrder(a: int32, b: int32): int32 {
    const first = new Accumulator(a);
    const second = new Accumulator(b);
    return first.current() * 100 + second.current();
}
