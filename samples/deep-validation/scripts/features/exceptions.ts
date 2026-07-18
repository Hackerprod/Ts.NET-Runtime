export function exceptionScenario(value: int32): int32 {
    let result: int32 = 1;
    try {
        if (value < 0) {
            throw "negative";
        }
        result = value * 2;
    } catch (error: string) {
        result = 40;
    } finally {
        result = result + 2;
    }
    return result;
}
