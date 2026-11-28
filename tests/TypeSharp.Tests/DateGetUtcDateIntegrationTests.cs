using System.Text;
using System.Text.RegularExpressions;
using TypeSharp.Hosting;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Hosting.StdLib;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class DateGetUtcDateIntegrationTests(ITestOutputHelper output)
{
    private const string JanuaryText = "2000-01-01T00:00:00.000Z";
    private const string LeapText = "2024-02-29T12:00:00.000Z";
    private const string OffsetText = "2000-01-01T00:30:00+01:00";

    [Fact]
    public async Task GetUtcDate_ReturnsOneBasedUtcDayAndInvalidNaN()
    {
        await WithAppAsync(
            "export function date(): any { const date = new Date(0); return date.getUTCDate(); }\n",
            async (dir, file) =>
            {
                var compilation = new TypeScriptCompilation(dir);
                compilation.AddSourceFile(file);
                compilation.Parse();
                output.WriteLine($"DateUtcDate stage=parse source={file}; count={compilation.Diagnostics.All.Count}");
                Assert.Empty(compilation.Diagnostics.All);

                var modules = compilation.Bind();
                output.WriteLine($"DateUtcDate stage=bind source={file}; count={compilation.Diagnostics.All.Count}");
                foreach (var diag in compilation.Diagnostics.All)
                {
                    var loc = diag.Location;
                    output.WriteLine($"severity={diag.Severity}; code={diag.Code}; numericCode={(int)diag.Code}; module={diag.ModuleId}; source={loc.Source}; line={loc.Line}; column={loc.Column}; offset={loc.Offset}; message={diag.Message}");
                }

                output.WriteLine($"DateUtcDate stage=hosting-build-start source={file}");
                await using var runtime = await BuildEpochAsync(dir);
                output.WriteLine("DateUtcDate stage=hosting-build-success");
                Assert.Empty(compilation.Diagnostics.All);
                var module = Assert.Single(modules);
                Assert.Equal("app", module.Key);
                var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(module.Value.Members));
                Assert.Equal("date", function.Symbol.Name);
                var body = Assert.IsType<BoundBlockStatement>(function.Body);
                var ret = Assert.Single(body.Statements.OfType<BoundReturnStatement>());
                var call = Assert.IsType<BoundCallExpression>(ret.Value);
                var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
                var method = Assert.IsType<MethodSymbol>(member.Member);
                Assert.Equal("Date", method.DeclaringClassName);
                Assert.Equal("getUTCDate", method.Name);
                Assert.False(method.IsStatic);
                Assert.Empty(method.Parameters);
                Assert.Empty(call.Arguments);
                Assert.Equal(TsType.Number, method.Type);
                Assert.Equal(TsType.Number, call.Type);

                var receiver = Assert.IsType<TsClassType>(member.Object.Type);
                Assert.Equal("Date", receiver.Name);
                output.WriteLine($"DateUtcDate stage=bound receiver={receiver.Name}; owner={method.DeclaringClassName}; method={method.Name}; static={method.IsStatic}; explicitParameters={method.Parameters.Count}; explicitArgs={call.Arguments.Count}; methodResult={method.Type.Name}; callResult={call.Type.Name}");

                output.WriteLine("DateUtcDate stage=epoch-invoke-start");
                var day = await runtime.InvokeAsync<double>("app", "date");
                LogNumber("epoch", "0", day);
                Assert.Equal(1d, day);
            });

        await WithAppAsync(
            "export function day(text: string): number { const d = new Date(text); return d.getUTCDate(); }\n",
            async (dir, file) =>
            {
                output.WriteLine($"DateUtcDate stage=typed-hosting-build-start source={file}");
                await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
                output.WriteLine("DateUtcDate stage=typed-hosting-build-success");
                foreach (var (row, input, expected) in new[]
                {
                    ("january", JanuaryText, 1d),
                    ("leap", LeapText, 29d),
                    ("rollover", OffsetText, 31d),
                    ("invalid", "not-a-date", double.NaN)
                })
                {
                    var actual = await runtime.InvokeAsync<double>("app", "day", input);
                    LogNumber(row, input, actual);
                    if (double.IsNaN(expected))
                        Assert.True(double.IsNaN(actual));
                    else
                        Assert.Equal(expected, actual);
                }
            });

        var capability = StandardLibraryManifest.Find("Date");
        Assert.NotNull(capability);
        Assert.Equal(StandardCapabilityStatus.Supported, capability.Status);
        Assert.Contains("getUTCDate()", capability.Signature);
        Assert.Contains("getUTCDate returns the UTC day of the month (1 through 31), or NaN if invalid.", capability.Semantics);
        Assert.Contains(nameof(GetUtcDate_ReturnsOneBasedUtcDayAndInvalidNaN), capability.Tests);

        var declarations = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var dateInterface = Assert.Single(Regex.Matches(declarations, @"(?m)^interface Date[ \t]*\{([^}]*)\}"));
        Assert.Single(Regex.Matches(dateInterface.Groups[1].Value,
            @"(?m)^[ \t]*getUTCDate\(\): number;\r?$"));
        output.WriteLine("DateUtcDate stage=metadata-success manifest=Supported; declaration=Date.getUTCDate(): number");
    }

    [Fact]
    public async Task ExistingUtcMonthAndYear_UseSameUtcInstant()
    {
        await WithAppAsync(
            "export function month(text: string): number { const d = new Date(text); return d.getUTCMonth(); }\n" +
            "export function year(text: string): number { const d = new Date(text); return d.getUTCFullYear(); }\n",
            async (dir, file) =>
            {
                output.WriteLine($"DateUtcDate stage=control-hosting-build-start source={file}");
                await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
                output.WriteLine("DateUtcDate stage=control-hosting-build-success");
                var month = await runtime.InvokeAsync<double>("app", "month", OffsetText);
                var year = await runtime.InvokeAsync<double>("app", "year", OffsetText);
                LogNumber("control-month", OffsetText, month);
                LogNumber("control-year", OffsetText, year);
                Assert.Equal(11d, month);
                Assert.Equal(1999d, year);
            });
    }

    private async Task<TypeSharpRuntime> BuildEpochAsync(string dir)
    {
        try
        {
            return await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine($"DateUtcDate stage=hosting-build-failure type={ex.GetType().FullName}");
            output.WriteLine(ex.Message);
            throw;
        }
    }

    private void LogNumber(string row, string input, double actual) =>
        output.WriteLine(FormattableString.Invariant($"DateUtcDate row={row}; input={input}; actual={actual:R}"));

    private static async Task WithAppAsync(string source, Func<string, string, Task> action)
    {
        var dir = Directory.CreateTempSubdirectory("date-utc-date-").FullName;
        try
        {
            var file = Path.Combine(dir, "app.ts");
            await File.WriteAllTextAsync(file, source, new UTF8Encoding(false));
            await action(dir, file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TypeSharp.sln")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate TypeSharp.sln.");
    }
}
