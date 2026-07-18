import { ImportedCounter } from "../model/counter";

export function importedClassScenario(): int32 {
    const counter = new ImportedCounter(40);
    return counter.next() + counter.next();
}
