export interface Address {
    number: int32;
    zip: int32;
}

export interface Person {
    id: int32;
    address: Address;
}

export function nestedStructural(): int32 {
    const person: Person = {
        id: 7,
        address: {
            number: 125,
            zip: 97201
        }
    };
    return person.id * 100000 + person.address.number * 100 + person.address.zip;
}

export function propertyMutation(): int32 {
    const address: Address = { number: 10, zip: 20 };
    address.number = 99;
    return address.number + address.zip;
}
