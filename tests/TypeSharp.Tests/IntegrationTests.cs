using TypeSharp.Hosting;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Hosting.HotReload;
using TypeSharp.Interop.HostExports;
using TypeSharp.Runtime.Generations;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task EndToEndFunction()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("samples/basic/main", "add", 3, 4);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task EndToEndFactorial()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("samples/basic/main", "factorial", 5);
        Assert.Equal(120, result);
    }

    [Fact]
    public async Task EndToEndFibonacci()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("samples/basic/main", "fibonacci", 10);
        Assert.Equal(55, result);
    }

    [Fact]
    public async Task EndToEndGreet()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<string>("samples/basic/main", "greet", "World");
        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public async Task EndToEndIsEven()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        Assert.True(await runtime.InvokeAsync<bool>("samples/basic/main", "isEven", 4));
        Assert.False(await runtime.InvokeAsync<bool>("samples/basic/main", "isEven", 7));
    }

    [Fact]
    public async Task HostServiceRegistration()
    {
        var builder = new TypeSharpRuntimeBuilder()
            .AddHostService("math", new MathHostService());
        await using var runtime = await builder.BuildAsync();

        Assert.NotNull(runtime);
    }

    [Fact]
    public async Task AsyncAwait_CanAwaitHostTaskThroughInvokeAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_async_host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                async function load(): Promise<string> {
                    const profile = await findProfile(42);
                    return profile;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .AddHostService("profiles", new AsyncProfileHost())
                .BuildAsync();

            var result = await runtime.InvokeAsync<string>(Path.GetFileName(dir), "load");
            Assert.Equal("profile:42", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AsyncAwait_AllowsAsyncCallbackHandlers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_async_callback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                interface HandlerContext {
                    readonly accountId: number;
                    reply(value: string): void;
                }

                async function on(handler: (ctx: HandlerContext) => void): Promise<boolean> {
                    const ctx: HandlerContext = {
                        accountId: 42,
                        reply: value => {
                            record(value);
                        }
                    };
                    await handler(ctx);
                    return true;
                }

                async function handle(): Promise<boolean> {
                    return await on(async ctx => {
                        const profile = await findProfile(ctx.accountId);
                        ctx.reply(profile);
                    });
                }
                """);

            var host = new AsyncProfileHost();
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .AddHostService("profiles", host)
                .BuildAsync();

            var handled = await runtime.InvokeAsync<bool>(Path.GetFileName(dir), "handle");
            Assert.True(handled);
            Assert.Equal("profile:42", host.LastReply);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HostNumber_CanBeComparedWithModuleConstant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_host_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                const expected = 4006;

                function matches(): boolean {
                    return messageType() == expected;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .RegisterHostFunction("gc", "messageType", _ => TsValue.FromInt32(4006))
                .BuildAsync();

            var result = runtime.Invoke("matches");
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result!).Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HostNumber_CanBeComparedWithNumericLiteral()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_host_literal_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                function matches(): boolean {
                    return messageType() == 4006;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .RegisterHostFunction("gc", "messageType", _ => TsValue.FromInt32(4006))
                .BuildAsync();

            var result = runtime.Invoke("matches");
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result!).Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NullCheck_NarrowsUnionInsidePositiveBranch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_null_narrow_positive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                interface CatalogItem {
                    readonly defIndex: number;
                }

                function main(): number {
                    const item: CatalogItem | null = getItem();
                    if (item !== null) {
                        return item.defIndex;
                    }

                    return 0;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .RegisterHostFunction("test", "getItem", _ =>
                {
                    var item = new TsObject("CatalogItem");
                    item.SetField("defIndex", TsValue.FromInt32(42));
                    return TsValue.FromObject(item);
                })
                .BuildAsync();

            var result = await runtime.InvokeAsync<double>(Path.GetFileName(dir), "main");
            Assert.Equal(42d, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NullCheck_NarrowsUnionInsideElseBranch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_null_narrow_else_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.ts");
        try
        {
            File.WriteAllText(file, """
                interface CatalogItem {
                    readonly defIndex: number;
                }

                function main(): number {
                    const item: CatalogItem | null = getItem();
                    if (item === null) {
                        return 0;
                    } else {
                        return item.defIndex;
                    }
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .RegisterHostFunction("test", "getItem", _ =>
                {
                    var item = new TsObject("CatalogItem");
                    item.SetField("defIndex", TsValue.FromInt32(64));
                    return TsValue.FromObject(item);
                })
                .BuildAsync();

            var result = await runtime.InvokeAsync<double>(Path.GetFileName(dir), "main");
            Assert.Equal(64d, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task IdiomaticCallbackRoute_CanUseExportedConstObjectAndInlineLambda()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_callback_route_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var generated = Path.Combine(dir, "generated.ts");
        var framework = Path.Combine(dir, "framework.ts");
        var app = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(generated, """
                export interface Route {
                    requestId: int32;
                }

                export const Result = {
                    Success: 7
                };

                export const Routes = {
                    Join: {
                        requestId: 4006
                    }
                };
                """);

            File.WriteAllText(framework, """
                import { Route } from "./generated";

                export class HandlerContext {
                    request: any;

                    constructor() {
                        this.request = {
                            channelName: "Lobby",
                            channelType: 3
                        };
                    }

                    reply(response: any): void {
                        record(response.result);
                    }
                }

                export function on(route: Route, handler: (ctx: HandlerContext) => void): boolean {
                    if (messageType() != route.requestId) {
                        return false;
                    }

                    const ctx = new HandlerContext();
                    handler(ctx);
                    return true;
                }
                """);

            File.WriteAllText(app, """
                import { Routes, Result } from "./generated";
                import { on } from "./framework";

                function handle(): boolean {
                    const offset = 2;
                    return on(Routes.Join, ctx => {
                        ctx.reply({
                            channelName: ctx.request.channelName,
                            channelType: ctx.request.channelType,
                            result: Result.Success + offset
                        });
                    });
                }
                """);

            int recorded = 0;
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .RegisterHostFunction("host", "messageType", _ => TsValue.FromInt32(4006))
                .RegisterHostFunction("host", "record", args =>
                {
                    recorded = args.Length > 0
                        ? args[0] switch
                        {
                            TsInt32Value int32 => int32.Value,
                            TsFloat64Value number => (int)number.Value,
                            _ => -1
                        }
                        : -1;
                    return TsValue.Void;
                })
                .BuildAsync();

            var result = runtime.Invoke("handle");
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result!).Value);
            Assert.Equal(9, recorded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ContextualLambdaParameter_UsesFunctionParameterTypeForMemberChecking()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_contextual_lambda_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "framework.ts"), """
                export class HandlerContext {
                    ok(): void {
                    }
                }

                export function on(handler: (ctx: HandlerContext) => void): void {
                    const ctx = new HandlerContext();
                    handler(ctx);
                }
                """);

            File.WriteAllText(Path.Combine(dir, "app.ts"), """
                import { on } from "./framework";

                function handle(): void {
                    on(ctx => {
                        ctx.missing();
                    });
                }
                """);

            var compilation = new TypeScriptCompilation(dir);
            compilation.AddSourceDirectory(dir);
            compilation.Compile();

            Assert.Contains(compilation.Diagnostics.GetErrors(),
                error => error.Message.Contains("No member 'missing'"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GenericRouteContext_PropagatesRequestAndResponseTypesIntoHandler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_generic_route_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "generated.ts"), """
                export interface JoinRequest {
                    readonly channelName: string;
                    readonly channelType: int32;
                }

                export interface JoinResponse {
                    readonly channelName: string;
                    readonly result: int32;
                }

                export interface Proto<TMessage> {
                    readonly name: string;
                }

                export interface Route<TRequest, TResponse> {
                    readonly requestId: int32;
                    readonly responseId: int32;
                    readonly request: Proto<TRequest>;
                    readonly response: Proto<TResponse>;
                }

                export const Proto = {
                    JoinRequest: { name: "JoinRequest" } as Proto<JoinRequest>,
                    JoinResponse: { name: "JoinResponse" } as Proto<JoinResponse>
                } as const;

                export const Routes = {
                    Join: {
                        requestId: 4006,
                        responseId: 4007,
                        request: Proto.JoinRequest,
                        response: Proto.JoinResponse
                    } as Route<JoinRequest, JoinResponse>
                } as const;
                """);

            File.WriteAllText(Path.Combine(dir, "framework.ts"), """
                import { Route } from "./generated";

                export interface HandlerContext<TRequest, TResponse> {
                    readonly request: TRequest;
                    reply(response: TResponse): void;
                }

                export const gc = {
                    on: <TRequest, TResponse>(route: Route<TRequest, TResponse>, handler: (ctx: HandlerContext<TRequest, TResponse>) => void): boolean => {
                        if (messageType() != route.requestId) {
                            return false;
                        }

                        const ctx = {
                            request: {
                                channelName: "Lobby",
                                channelType: 3
                            },
                            reply: (response: any): void => {
                                record(response.result);
                            }
                        } as HandlerContext<TRequest, TResponse>;
                        handler(ctx);
                        return true;
                    }
                } as const;
                """);

            File.WriteAllText(Path.Combine(dir, "app.ts"), """
                import { Routes } from "./generated";
                import { gc } from "./framework";

                function handle(): boolean {
                    return gc.on(Routes.Join, ctx => {
                        ctx.reply({
                            channelName: ctx.request.channelName,
                            result: ctx.request.channelType + 4
                        });
                    });
                }
                """);

            int recorded = 0;
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .RegisterHostFunction("host", "messageType", _ => TsValue.FromInt32(4006))
                .RegisterHostFunction("host", "record", args =>
                {
                    recorded = args[0] switch
                    {
                        TsInt32Value int32 => int32.Value,
                        TsInt64Value int64 => (int)int64.Value,
                        TsUInt64Value uint64 => (int)uint64.Value,
                        TsFloat32Value float32 => (int)float32.Value,
                        TsFloat64Value number => (int)number.Value,
                        TsNull => -998,
                        TsVoid => -999,
                        _ => -1
                    };
                    return TsValue.Void;
                })
                .BuildAsync();

            var result = runtime.Invoke("handle");
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result!).Value);
            Assert.Equal(7, recorded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Map_SupportsTypedLookupMutationAndSameValueZeroKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_map_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), """
                function main(): number {
                    const handlers = new Map<any, number>();
                    handlers.set(10, 1).set("10", 2);

                    if (!handlers.has(10) || !handlers.has("10")) {
                        return -1;
                    }

                    const numeric = handlers.get(10) as number;
                    const text = handlers.get("10") as number;
                    const beforeDelete = handlers.size;
                    const deleted = handlers.delete(10);
                    const stillHasNumeric = handlers.has(10);

                    handlers.clear();

                    return numeric + text + beforeDelete + handlers.size +
                        (deleted ? 10 : 0) +
                        (stillHasNumeric ? 100 : 0);
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .BuildAsync();

            var result = await runtime.InvokeAsync<double>("app", "main");
            Assert.Equal(15, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StringConcatenation_AllowsPrimitiveOperands()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_string_concat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), """
                function main(): string {
                    return "message=" + 4006 + " steam=" + 76561197960265728n + " ok=" + true;
                }
                """);

            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .BuildAsync();

            var result = await runtime.InvokeAsync<string>("app", "main");
            Assert.Equal("message=4006 steam=76561197960265728 ok=true", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportedConstWithLambda_CanBeImportedByMultipleModulesInLinkedGeneration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_imported_const_lambda_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "generated.ts"), """
                export interface Route {
                    readonly requestId: int32;
                }

                export const Routes = {
                    A: { requestId: 4006 } as Route,
                    B: { requestId: 4006 } as Route
                } as const;
                """);

            File.WriteAllText(Path.Combine(dir, "framework.ts"), """
                import { Route } from "./generated";

                export interface HandlerContext {
                    readonly requestId: int32;
                    reply(value: int32): void;
                }

                export const gc = {
                    on: (route: Route, handler: (ctx: HandlerContext) => void): boolean => {
                        if (messageType() != route.requestId) {
                            return false;
                        }

                        const ctx = {
                            requestId: route.requestId,
                            reply: (value: int32): void => {
                                record(value);
                            }
                        } as HandlerContext;

                        handler(ctx);
                        return true;
                    }
                } as const;
                """);

            File.WriteAllText(Path.Combine(dir, "moduleA.ts"), """
                import { Routes } from "./generated";
                import { gc } from "./framework";

                export function handleA(): boolean {
                    return gc.on(Routes.A, ctx => {
                        ctx.reply(10);
                    });
                }
                """);

            File.WriteAllText(Path.Combine(dir, "moduleB.ts"), """
                import { Routes } from "./generated";
                import { gc } from "./framework";

                export function handleB(): boolean {
                    return gc.on(Routes.B, ctx => {
                        ctx.reply(20);
                    });
                }
                """);

            File.WriteAllText(Path.Combine(dir, "main.ts"), """
                import { handleA } from "./moduleA";
                import { handleB } from "./moduleB";

                function run(): int32 {
                    if (!handleA()) return 1;
                    if (!handleB()) return 2;
                    return 3;
                }
                """);

            var recorded = new List<int>();
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .RegisterHostFunction("host", "messageType", _ => TsValue.FromInt32(4006))
                .RegisterHostFunction("host", "record", args =>
                {
                    recorded.Add(args[0] switch
                    {
                        TsInt32Value int32 => int32.Value,
                        TsInt64Value int64 => (int)int64.Value,
                        TsFloat64Value number => (int)number.Value,
                        _ => -1
                    });
                    return TsValue.Void;
                })
                .BuildAsync();

            var result = runtime.Invoke("run");
            Assert.Equal(3, ((TsInt32Value)result!).Value);
            Assert.Equal(new[] { 10, 20 }, recorded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ImportedLargeModuleConstants_AreLoadedFromModuleGlobals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_large_module_constant_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var routeEntries = string.Join(Environment.NewLine, Enumerable.Range(0, 250).Select(i =>
                $"                    R{i}: {{ requestId: {4000 + i}, responseId: {5000 + i} }} as Route,"));

            var generatedSource = """
                export interface Route {
                    readonly requestId: int32;
                    readonly responseId: int32;
                }

                export const Routes = {
                """ + Environment.NewLine + routeEntries + Environment.NewLine + """
                } as const;
                """;
            File.WriteAllText(Path.Combine(dir, "generated.ts"), generatedSource);

            File.WriteAllText(Path.Combine(dir, "framework.ts"), """
                import { Route } from "./generated";

                export interface HandlerContext {
                    readonly messageId: int32;
                    reply(value: int32): void;
                }

                export type Handler = (ctx: HandlerContext) => boolean;

                interface Registration {
                    readonly messageId: int32;
                    readonly handler: Handler;
                }

                class Router {
                    handlers: Map<int32, Registration>;

                    constructor() {
                        this.handlers = new Map<int32, Registration>();
                    }

                    on(route: Route, handler: Handler): void {
                        this.handlers.set(route.requestId, {
                            messageId: route.requestId,
                            handler
                        } as Registration);
                    }

                    dispatch(): boolean {
                        const current = messageType();
                        if (!this.handlers.has(current)) {
                            return false;
                        }

                        const registration = this.handlers.get(current) as Registration;
                        return registration.handler({
                            messageId: current,
                            reply: value => {
                                record(value);
                            }
                        } as HandlerContext);
                    }
                }

                export const gc = new Router();
                """);

            File.WriteAllText(Path.Combine(dir, "social.ts"), """
                import { gc } from "./framework";
                import { Routes } from "./generated";

                export function registerSocial(): void {
                    const social = new Social();
                    social.register();
                }

                export class Social {
                    register(): void {
                        gc.on(Routes.R6, ctx => this.reply(ctx, 6));
                        gc.on(Routes.R7, ctx => this.reply(ctx, 7));
                        gc.on(Routes.R8, ctx => this.reply(ctx, 8));
                    }

                    reply(ctx: any, value: int32): boolean {
                        ctx.reply(value);
                        return true;
                    }
                }
                """);

            File.WriteAllText(Path.Combine(dir, "main.ts"), """
                import { gc } from "./framework";
                import { registerSocial } from "./social";

                registerSocial();

                export function handle(): boolean {
                    return gc.dispatch();
                }
                """);

            var recorded = new List<int>();
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .RegisterHostFunction("host", "messageType", _ => TsValue.FromInt32(4006))
                .RegisterHostFunction("host", "record", args =>
                {
                    recorded.Add(args[0] switch
                    {
                        TsInt32Value int32 => int32.Value,
                        TsInt64Value int64 => (int)int64.Value,
                        TsFloat64Value number => (int)number.Value,
                        _ => -1
                    });
                    return TsValue.Void;
                })
                .BuildAsync();

            var socialModule = runtime.ActiveGeneration!.Modules["social"].Bytecode;
            var registerFunction = socialModule.Functions.Single(f => f.Name == "Social::register");
            Assert.True(registerFunction.Instructions.Length < 4096,
                $"Social::register bytecode should load Routes from module storage instead of inlining it; actual length was {registerFunction.Instructions.Length}.");

            var result = runtime.Invoke("handle");
            Assert.True(((TsBoolValue)result!).Value);
            Assert.Equal(new[] { 6 }, recorded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadonlyMembers_AreRejectedOnAssignment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_readonly_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), """
                interface Route {
                    readonly requestId: int32;
                }

                function mutate(route: Route): void {
                    route.requestId = 1;
                }
                """);

            var compilation = new TypeScriptCompilation(dir);
            compilation.AddSourceDirectory(dir);
            compilation.Compile();

            Assert.Contains(compilation.Diagnostics.GetErrors(),
                error => error.Message.Contains("readonly member 'requestId'"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadonlyObjectTypeMembers_AreAcceptedAndRejectedOnAssignment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_readonly_object_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), """
                type Route = {
                    readonly requestId: number;
                    readonly request?: { readonly name: string };
                };

                function count(slots: readonly { readonly slotId?: number }[]): number {
                    return slots.length;
                }

                function mutateArray(slots: readonly { readonly slotId?: number }[]): void {
                    slots.push({ slotId: 1 });
                }

                function read(route: Route): number {
                    return route.requestId;
                }

                function mutate(route: Route): void {
                    route.requestId = 1;
                }
                """);

            var compilation = new TypeScriptCompilation(dir);
            compilation.AddSourceDirectory(dir);
            compilation.Compile();

            Assert.Contains(
                compilation.Diagnostics.GetErrors(),
                error => error.Message.Contains("readonly member 'requestId'"));
            Assert.Contains(
                compilation.Diagnostics.GetErrors(),
                error => error.Message.Contains("No member 'push'"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task HotReload_PublishesValidatedCandidateAndRollsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_reload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .BuildAsync();

            Assert.Equal(1, ((TsInt32Value)runtime.Invoke("value")!).Value);
            var initial = runtime.ActiveGeneration;

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.True(await runtime.ReloadAsync(file));
            Assert.Equal(2, ((TsInt32Value)runtime.Invoke("value")!).Value);
            Assert.NotSame(initial, runtime.ActiveGeneration);
            Assert.Contains(initial!, runtime.RetiredGenerations);

            File.WriteAllText(file, "function value(): string { return 3; }");
            Assert.False(await runtime.ReloadAsync(file));
            Assert.Equal(2, ((TsInt32Value)runtime.Invoke("value")!).Value);

            Assert.True(await runtime.RollbackAsync());
            Assert.Equal(1, ((TsInt32Value)runtime.Invoke("value")!).Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_CanaryRejectsCandidateWithoutPublishing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_canary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .ConfigureHotReload(options => options.Canary = _ => false)
                .BuildAsync();

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.False(await runtime.ReloadAsync(file));
            Assert.Equal(1, ((TsInt32Value)runtime.Invoke("value")!).Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_GenerationLeaseTracksActiveExecutions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_lease_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .BuildAsync();

            var generation = runtime.ActiveGeneration!;
            using (runtime.AcquireGeneration())
                Assert.Equal(1, generation.ActiveExecutions);
            Assert.Equal(0, generation.ActiveExecutions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FunctionHandle_InvokesTheGenerationThatCreatedIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_handle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .BuildAsync();

            var handle = runtime.CreateFunctionHandle("value");
            Assert.Equal(1, handle.GenerationId);

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.True(await runtime.ReloadAsync(file));

            Assert.False(handle.IsCurrent);
            Assert.Equal(1, ((TsInt32Value)handle.Invoke()!).Value);
            Assert.Equal(2, ((TsInt32Value)runtime.Invoke("value")!).Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_RunsExplicitMigratorBeforePublish()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            var migrator = new TrackingMigrator();
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .ConfigureHotReload(options => options.Migrators.Add(migrator))
                .BuildAsync();

            File.WriteAllText(file, "function value(): int32 { return 2; }");
            Assert.True(await runtime.ReloadAsync(file));
            Assert.True(migrator.WasMigrated);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HotReload_RetainsLeasedGenerationUntilExecutionCompletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_retain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "app.ts");
        try
        {
            File.WriteAllText(file, "function value(): int32 { return 1; }");
            await using var runtime = await new TypeSharpRuntimeBuilder()
                .AddSourceFile(file)
                .EnableHotReload()
                .ConfigureHotReload(options => options.RetainedGenerations = 0)
                .BuildAsync();

            using (runtime.AcquireGeneration())
            {
                File.WriteAllText(file, "function value(): int32 { return 2; }");
                Assert.True(await runtime.ReloadAsync(file));
                Assert.Single(runtime.RetiredGenerations);
            }

            Assert.Empty(runtime.RetiredGenerations);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class TrackingMigrator : IGenerationMigrator
    {
        public bool WasMigrated { get; private set; }
        public bool CanMigrate(RuntimeGeneration previous, RuntimeGeneration candidate) => true;
        public void Migrate(RuntimeGeneration previous, RuntimeGeneration candidate) => WasMigrated = true;
    }
}

public class ModuleNamingTests
{
    [Fact]
    public void ComputeModuleId_TopLevelFile()
    {
        var id = TypeScriptCompilation.ComputeModuleId("/project/users.ts", "/project");
        Assert.Equal("users", id);
    }

    [Fact]
    public void ComputeModuleId_NestedFile()
    {
        var id = TypeScriptCompilation.ComputeModuleId("/project/services/users.ts", "/project");
        Assert.Equal("services/users", id);
    }

    [Fact]
    public void ComputeModuleId_IndexFile()
    {
        var id = TypeScriptCompilation.ComputeModuleId("/project/services/users/index.ts", "/project");
        Assert.Equal("services/users/index", id);
    }

    [Fact]
    public void ComputeModuleId_NoCollision()
    {
        var id1 = TypeScriptCompilation.ComputeModuleId("/project/scripts/a.ts", "/project");
        var id2 = TypeScriptCompilation.ComputeModuleId("/project/scripts/b.ts", "/project");
        Assert.NotEqual(id1, id2);
        Assert.Equal("scripts/a", id1);
        Assert.Equal("scripts/b", id2);
    }
}

public class CompilationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typesharp_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Compilation_ParsesMultipleFiles()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.ts"), "function add(a: int32, b: int32): int32 { return a + b; }");
            File.WriteAllText(Path.Combine(dir, "b.ts"), "function main(): int32 { return 42; }");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors);
            Assert.Equal(2, comp.CompiledModules.Count);
            Assert.Contains("a", comp.CompiledModules.Keys);
            Assert.Contains("b", comp.CompiledModules.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_IgnoresDeclarationFiles()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "host.d.ts"), """
                declare module "@runtime/host" {
                    export function now(): number;
                }
                declare function messageType(): number;
                """);
            File.WriteAllText(Path.Combine(dir, "app.ts"), "function main(): number { return 42; }");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors);
            Assert.Single(comp.CompiledModules);
            Assert.Contains("app", comp.CompiledModules.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_CanonicalModuleIds()
    {
        var dir = TempDir();
        try
        {
            var sub = Path.Combine(dir, "services");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "users.ts"), "function main(): int32 { return 1; }");
            File.WriteAllText(Path.Combine(sub, "orders.ts"), "function main(): int32 { return 2; }");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            comp.Compile();

            Assert.Contains("services/users", comp.CompiledModules.Keys);
            Assert.Contains("services/orders", comp.CompiledModules.Keys);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassesModule()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Counter {
    count: int32;
    constructor(initial: int32) {
        this.count = initial;
    }
    getCount(): int32 {
        return this.count;
    }
}
function main(): int32 {
    const c = new Counter(10);
    return c.getCount();
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceDirectory(dir);
            var modules = comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors);
            Assert.Single(modules);
            Assert.Contains("Counter::.ctor", modules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Counter::getCount", modules["app"].Bytecode.FunctionIndex);
            Assert.Contains("main", modules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ImportBinding_CrossModule()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "math.ts"), @"
export function add(a: int32, b: int32): int32 {
    return a + b;
}
export function multiply(a: int32, b: int32): int32 {
    return a * b;
}
");
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
import { add, multiply } from './math';

function main(): int32 {
    const x = add(3, 4);
    const y = multiply(5, 6);
    return x + y;
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "math.ts"));
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            var errors = comp.Diagnostics.GetErrors().ToList();
            Assert.False(comp.Diagnostics.HasErrors, "Errors: " + string.Join("; ", errors.Select(e => e.Message)));
            Assert.Equal(2, comp.CompiledModules.Count);
            Assert.Contains("math", comp.CompiledModules.Keys);
            Assert.Contains("app", comp.CompiledModules.Keys);
            Assert.Contains("add", comp.CompiledModules["math"].Bytecode.FunctionIndex);
            Assert.Contains("multiply", comp.CompiledModules["math"].Bytecode.FunctionIndex);
            Assert.Contains("main", comp.CompiledModules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassMethodCanCallLaterMethod()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "worker.ts"), @"
export class Worker {
    run(value: int32): boolean {
        return this.isExpected(value);
    }

    isExpected(value: int32): boolean {
        return value == 42;
    }
}
");
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
import { Worker } from './worker';

function main(): boolean {
    const worker = new Worker();
    return worker.run(42);
}
");

            var runtime = new TypeSharpRuntimeBuilder()
                .AddSourceDirectory(dir)
                .BuildAsync()
                .GetAwaiter()
                .GetResult();

            var result = runtime.Invoke("main");

            Assert.NotNull(result);
            Assert.IsType<TsBoolValue>(result);
            Assert.True(((TsBoolValue)result).Value);

            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ImportBinding_SecondModule()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "utils.ts"), @"
export function greet(name: string): string {
    return 'Hello ' + name;
}
");
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
import { greet } from './utils';

function main(): string {
    return greet('TypeSharp');
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "utils.ts"));
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            var errors = comp.Diagnostics.GetErrors().ToList();
            Assert.False(comp.Diagnostics.HasErrors, "Errors: " + string.Join("; ", errors.Select(e => e.Message)));
            Assert.Contains("greet", comp.CompiledModules["utils"].Bytecode.FunctionIndex);
            Assert.Contains("main", comp.CompiledModules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassHierarchy_BaseConstructor()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Animal {
    name: string;
    constructor(name: string) {
        this.name = name;
    }
    speak(): string {
        return this.name + ' makes a noise';
    }
}

class Dog extends Animal {
    breed: string;
    constructor(name: string, breed: string) {
        super(name);
        this.breed = breed;
    }
    speak(): string {
        return this.name + ' barks';
    }
}

function main(): string {
    const d = new Dog('Rex', 'Labrador');
    return d.speak();
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            Assert.False(comp.Diagnostics.HasErrors, "Errors: " + string.Join("; ", comp.Diagnostics.GetErrors().Select(e => e.Message)));
            Assert.Contains("Animal::.ctor", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Dog::.ctor", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Animal::speak", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("Dog::speak", comp.CompiledModules["app"].Bytecode.FunctionIndex);
            Assert.Contains("main", comp.CompiledModules["app"].Bytecode.FunctionIndex);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassHierarchy_RejectsIncompatibleOverride()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Base {
    describe(value: int32): string { return 'base'; }
}
class Derived extends Base {
    describe(value: string): int32 { return 1; }
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            Assert.Contains(comp.Diagnostics.GetErrors(), d => d.Code == TypeSharp.Syntax.Diagnostics.DiagnosticCode.TS2013);
            Assert.Contains(comp.Diagnostics.GetErrors(), d => d.Code == TypeSharp.Syntax.Diagnostics.DiagnosticCode.TS2015);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Compilation_ClassHierarchy_RejectsSuperOutsideConstructor()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.ts"), @"
class Base { }
class Derived extends Base {
    initialize(): void { super(); }
}
");

            var comp = new TypeScriptCompilation(dir);
            comp.AddSourceFile(Path.Combine(dir, "app.ts"));
            comp.Compile();

            Assert.Contains(comp.Diagnostics.GetErrors(), d => d.Code == TypeSharp.Syntax.Diagnostics.DiagnosticCode.TS2001);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class MathHostService
{
    public int Add(int a, int b) => a + b;
    public int Multiply(int a, int b) => a * b;
}

public class AsyncProfileHost
{
    public string? LastReply { get; private set; }

    [TsExport("findProfile")]
    public Task<string> FindProfile(double accountId) => Task.FromResult($"profile:{(int)accountId}");

    [TsExport("record")]
    public void Record(string value) => LastReply = value;
}


