using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace TypeSharp.Tests;

[CollectionDefinition("CLI watch arguments", DisableParallelization = true)]
public sealed class CliWatchArgumentsCollection { }

[Collection("CLI watch arguments")]
public sealed class CliWatchArgumentsTests
{
    private const string Usage = "Usage: typesharp --watch <directory>";
    private const string Script = """export function main(): string { return "ok"; }""";

    [Fact]
    public async Task BareWatchReportsUsage() =>
        await VerifyAsync(["--watch"], 1, Usage);

    [Fact]
    public async Task MissingWatchDirectoryReportsOperand() =>
        await VerifyAsync(["--watch", "./absent"], 1, "Error: Watch directory does not exist: ./absent");

    [Fact]
    public async Task RegularFileIsNotAWatchDirectory() =>
        await VerifyAsync(["--watch", "./echo.ts"], 1, "Error: Watch directory does not exist: ./echo.ts");

    [Fact]
    public async Task ExtraWatchOperandReportsUsageBeforeCheckingDirectory() =>
        await VerifyAsync(["--watch", "./absent", "extra"], 1, Usage);

    [Fact]
    public async Task NoArgumentsStillPrintHelp() =>
        await VerifyAsync([], 0, null,
            "Usage: typesharp <file.ts> [function] [args...]",
            "       typesharp --watch <directory>",
            "       Function arguments are passed as strings.");

    [Fact]
    public async Task OrdinaryFileStillExecutesImplicitMain() =>
        await VerifyAsync(["echo.ts"], 0, null, "Executing echo.ts -> main...", "Result: ok");

    private static async Task VerifyAsync(string[] args, int expectedExitCode, string? error, params string[] output)
    {
        var fixture = Directory.CreateTempSubdirectory("typesharp-cli-watch-");
        try
        {
            File.WriteAllText(Path.Combine(fixture.FullName, "echo.ts"), Script);
            var previousDirectory = Directory.GetCurrentDirectory();
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExitCode = Environment.ExitCode;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode;
            try
            {
                Directory.SetCurrentDirectory(fixture.FullName);
                Console.SetOut(stdout);
                Console.SetError(stderr);
                Environment.ExitCode = 0;
                await TypeSharp.Hosting.Program.Main(args);
                exitCode = Environment.ExitCode;
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                Environment.ExitCode = previousExitCode;
            }

            string nl = Environment.NewLine;
            string banner = "TypeSharp Runtime v0.1.0" + nl + "========================" + nl;
            Assert.Equal(error is null ? "" : error + nl, stderr.ToString());
            Assert.Equal(banner + (output.Length == 0 ? "" : string.Join(nl, output) + nl), stdout.ToString());
            Assert.Equal(expectedExitCode, exitCode);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }
}
