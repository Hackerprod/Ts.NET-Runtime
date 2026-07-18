export class ImportedCounter {
    private value: int32;

    constructor(initial: int32) {
        this.value = initial;
    }

    next(): int32 {
        this.value = this.value + 1;
        return this.value;
    }
}
