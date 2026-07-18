using TypeSharp.Hosting;
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

        var result = await runtime.InvokeAsync<int>("basic", "add", 3, 4);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task EndToEndFactorial()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("basic", "factorial", 5);
        Assert.Equal(120, result);
    }

    [Fact]
    public async Task EndToEndFibonacci()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<int>("basic", "fibonacci", 10);
        Assert.Equal(55, result);
    }

    [Fact]
    public async Task EndToEndGreet()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        var result = await runtime.InvokeAsync<string>("basic", "greet", "World");
        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public async Task EndToEndIsEven()
    {
        var builder = new TypeSharpRuntimeBuilder();
        await using var runtime = await builder
            .AddSourceFile("samples/basic/main.ts")
            .BuildAsync();

        Assert.True(await runtime.InvokeAsync<bool>("basic", "isEven", 4));
        Assert.False(await runtime.InvokeAsync<bool>("basic", "isEven", 7));
    }

    [Fact]
    public async Task HostServiceRegistration()
    {
        var builder = new TypeSharpRuntimeBuilder()
            .AddHostService("math", new MathHostService());
        await using var runtime = await builder.BuildAsync();

        Assert.NotNull(runtime);
    }
}

public class MathHostService
{
    public int Add(int a, int b) => a + b;
    public int Multiply(int a, int b) => a * b;
}
