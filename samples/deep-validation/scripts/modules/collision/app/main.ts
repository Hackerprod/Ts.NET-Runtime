import { leftValue } from "../left/shared";
import { rightValue } from "../right/shared";

export function collisionScenario(): int32 {
    return leftValue() * 100 + rightValue();
}
