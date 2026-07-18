export class Counter {
    constructor(value: int32) {
    }
}

export function invalid(): int32 {
    const counter = new Counter("wrong");
    return 1;
}
