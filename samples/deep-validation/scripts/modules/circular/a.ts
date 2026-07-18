import { fromB } from "./b";

export function fromA(): int32 {
    return fromB() + 1;
}
