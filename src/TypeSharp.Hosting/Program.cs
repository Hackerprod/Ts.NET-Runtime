using TypeSharp.Hosting;

namespace TypeSharp.Hosting;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("TypeSharp Runtime v0.1.0");
        Console.WriteLine("========================");

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: typesharp <file.ts> [function] [args...]");
            Console.WriteLine("       typesharp --watch <directory>");
            return;
        }

        if (args[0] == "--watch" && args.Length > 1)
        {
            await RunWatchMode(args[1]);
        }
        else
        {
            await RunFile(args[0], args.Skip(1).ToArray());
        }
    }

    private static async Task RunFile(string filePath, string[] args)
    {
        try
        {
            var builder = new TypeSharpRuntimeBuilder()
                .ConfigureLimits(options =>
                {
                    options.ExecutionTimeout = TimeSpan.FromSeconds(30);
                    options.MaximumInstructions = 10_000_000;
                    options.MaximumMemoryBytes = 64 * 1024 * 1024;
                });

            await using var runtime = await builder
                .AddSourceFile(filePath)
                .BuildAsync();

            string entryFunction = args.Length > 0 ? args[0] : "main";

            var module = await runtime.ImportAsync(Path.GetFileNameWithoutExtension(filePath));

            Console.WriteLine($"Executing {filePath} -> {entryFunction}...");

            var result = runtime.Invoke(entryFunction);
            Console.WriteLine($"Result: {result}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunWatchMode(string directory)
    {
        Console.WriteLine($"Watching {directory} for changes...");

        var builder = new TypeSharpRuntimeBuilder()
            .AddSourceDirectory(directory)
            .EnableHotReload();

        await using var runtime = await builder.BuildAsync();

        var hotReload = new HotReload.HotReloadService(runtime);
        hotReload.ModuleReloaded += (s, e) =>
        {
            Console.WriteLine($"[HotReload] Module '{e.ModuleName}' reloaded (gen {e.NewGenerationId})");
        };

        hotReload.StartWatching(directory);

        Console.WriteLine("Press Ctrl+C to stop...");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        hotReload.StopWatching();
    }
}
