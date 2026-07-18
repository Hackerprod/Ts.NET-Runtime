export function precedence(): int32 {
    return 2 + 3 * 4 - 5;
}

export function groupedPrecedence(): int32 {
    return (2 + 3) * (4 - 1);
}

export function leftAssociative(): int32 {
    return 40 - 9 - 6;
}

export function forSum(limit: int32): int32 {
    let total: int32 = 0;
    for (let i: int32 = 1; i <= limit; i = i + 1) {
        total = total + i;
    }
    return total;
}

export function nestedLoops(rows: int32, columns: int32): int32 {
    let total: int32 = 0;
    for (let r: int32 = 0; r < rows; r = r + 1) {
        for (let c: int32 = 0; c < columns; c = c + 1) {
            total = total + r * 10 + c;
        }
    }
    return total;
}

export function forwardReference(value: int32): int32 {
    return declaredLater(value) + 2;
}

export function declaredLater(value: int32): int32 {
    return value * 5;
}

export function isEven(value: int32): bool {
    if (value == 0) {
        return true;
    }
    return isOdd(value - 1);
}

export function isOdd(value: int32): bool {
    if (value == 0) {
        return false;
    }
    return isEven(value - 1);
}

export function earlyReturn(value: int32): int32 {
    if (value < 0) {
        return -1;
    }
    if (value == 0) {
        return 0;
    }
    return value * 2;
}

export function branchMerge(value: int32): int32 {
    let result: int32 = 10;
    if (value > 5) {
        result = result + 7;
    } else {
        result = result - 3;
    }
    return result * 2;
}

export function logicalPrecedence(a: bool, b: bool, c: bool): bool {
    return a || b && c;
}

export function recursiveSum(value: int32): int32 {
    if (value <= 0) {
        return 0;
    }
    return value + recursiveSum(value - 1);
}

export function objectPerCall(value: int32): int32 {
    const item = { x: value, y: value + 1 };
    return item.x + item.y;
}
