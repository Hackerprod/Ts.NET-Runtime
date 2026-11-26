using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace TypeSharp.Tests;

[CollectionDefinition("Hosting CLI function arguments", DisableParallelization = true)]
public sealed class CliFunctionArgumentsCollection
{
}

[Collection("Hosting CLI function arguments")]
public sealed class CliFunctionArgumentsTests
{
    [Fact]
    public async Task FunctionArguments_ArePassedAsExactStrings()
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-cli-args-");
        try
        {
            var file = Path.Combine(directory.FullName, "echo.ts");
            await File.WriteAllTextAsync(file,
                """export function render(first = "DEFAULT-1", second = "DEFAULT-2"): string { return first + "|" + second; }""");

            await AssertInvocationAsync(file, [file, "render"], "DEFAULT-1|DEFAULT-2");
            await AssertInvocationAsync(file, [file, "render", "007", "two words"], "007|two words");
            await AssertInvocationAsync(file, [file, "render", ""], "|DEFAULT-2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task AssertInvocationAsync(string file, string[] args, string expectedResult)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Environment.ExitCode = 0;
            await TypeSharp.Hosting.Program.Main(args);
            exitCode = Environment.ExitCode;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
        }

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        using var output = new StringReader(stdout.ToString());
        Assert.Equal("TypeSharp Runtime v0.1.0", output.ReadLine());
        Assert.Equal("========================", output.ReadLine());
        Assert.Equal($"Executing {file} -> render...", output.ReadLine());
        Assert.Equal($"Result: {expectedResult}", output.ReadLine());
        Assert.Null(output.ReadLine());
    }
}
