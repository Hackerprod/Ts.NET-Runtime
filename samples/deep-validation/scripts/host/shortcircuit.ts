export function andShortCircuit(): bool {
    return false && sideEffectTrue();
}

export function orShortCircuit(): bool {
    return true || sideEffectFalse();
}
