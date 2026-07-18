export function compound(value: int32): int32 {
    let result: int32 = value;
    result += 5;
    result *= 3;
    result -= 4;
    result /= 2;
    return result;
}
