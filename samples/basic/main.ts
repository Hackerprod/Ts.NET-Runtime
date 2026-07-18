export interface User {
    id: int32;
    name: string;
    email: string;
}

export class UserService {
    private users: Map<int32, User>;

    constructor() {
        this.users = new Map<int32, User>();
    }

    add(user: User): void {
        this.users.set(user.id, user);
    }

    find(id: int32): User? {
        return this.users.get(id);
    }
}

export function main(): int32 {
    const service = new UserService();
    const user: User = { id: 1, name: "Alice", email: "alice@example.com" };
    service.add(user);
    const found = service.find(1);
    return found?.id ?? 0;
}

export function add(a: int32, b: int32): int32 {
    return a + b;
}

export function factorial(n: int32): int32 {
    if (n <= 1) {
        return 1;
    }
    return n * factorial(n - 1);
}

export function fibonacci(n: int32): int32 {
    let a: int32 = 0;
    let b: int32 = 1;
    let i: int32 = 0;
    while (i < n) {
        const temp: int32 = b;
        b = a + b;
        a = temp;
        i = i + 1;
    }
    return a;
}

export function greet(name: string): string {
    return "Hello " + name + "!";
}

export function isEven(n: int32): bool {
    return n % 2 == 0;
}
