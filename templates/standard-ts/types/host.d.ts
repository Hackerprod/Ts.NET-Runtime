import "@runtime/host";

declare module "@runtime/host" {
    export interface Capabilities {
        readonly profiles: ProfileService;
        readonly clock: ClockService;
    }

    export interface ProfileService {
        find(accountId: number): Promise<Profile | null>;
    }

    export interface ClockService {
        now(): number;
    }

    export interface Profile {
        readonly accountId: number;
        readonly displayName: string;
    }
}

export {};
