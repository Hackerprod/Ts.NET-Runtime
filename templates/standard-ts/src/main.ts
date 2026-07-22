import { capability } from "@runtime/host";

interface LoadProfileContext {
    readonly accountId: number;
    reply(payload: RuntimeJsonValue): void;
}

const profiles = capability("profiles");
const clock = capability("clock");

export function loadProfile(ctx: LoadProfileContext): boolean {
    const profile = profiles.find(ctx.accountId);

    if (profile === null) {
        ctx.reply({
            ok: false,
            accountId: ctx.accountId,
            requestedAt: clock.now()
        });
        return false;
    }

    ctx.reply({
        ok: true,
        accountId: profile.accountId,
        displayName: profile.displayName,
        requestedAt: clock.now()
    });
    return true;
}
