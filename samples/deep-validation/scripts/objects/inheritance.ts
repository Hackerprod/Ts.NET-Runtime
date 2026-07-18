export class BaseCounter {
    protected value: int32;

    constructor(initial: int32) {
        this.value = initial;
    }

    add(amount: int32): int32 {
        this.value = this.value + amount;
        return this.value;
    }

    read(): int32 {
        return this.value;
    }
}

export class StepCounter extends BaseCounter {
    private step: int32;

    constructor(initial: int32, step: int32) {
        super(initial);
        this.step = step;
    }

    advance(): int32 {
        return this.add(this.step);
    }
}

export function inheritanceScenario(): int32 {
    const counter = new StepCounter(10, 3);
    const first: int32 = counter.advance();
    const second: int32 = counter.advance();
    return first * 100 + second;
}
