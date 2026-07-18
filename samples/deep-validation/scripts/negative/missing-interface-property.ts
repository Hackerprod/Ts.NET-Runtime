export interface Point {
    x: int32;
    y: int32;
}

export function invalid(): int32 {
    const point: Point = { x: 4 };
    return point.x;
}
