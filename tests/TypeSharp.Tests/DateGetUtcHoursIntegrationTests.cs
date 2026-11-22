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

public sealed class DateGetUtcHoursIntegrationTests(ITestOutputHelper output)
{
    private const string MiddayText = "2024-02-25T12:15:00.000Z";
    private const string MidnightText = "2000-01-01T00:30:00.000Z";
    private const string OffsetText = "2000-01-01T00:30:00+01:00";

    [Fact]
    public async Task GetUtcHours_ReturnsHourOfStoredUtcInstant()
    {
        await WithAppAsync(
            "export function date(): any { const date = new Date(0); return date.getUTCHours(); }\n",
            async (dir, file) =>
            {
                var compilation = new TypeScriptCompilation(dir);
                compilation.AddSourceFile(file);
                compilation.Parse();
                output.WriteLine($"DateUtcHours stage=parse source={file}; count={compilation.Diagnostics.All.Count}");
                Assert.Empty(compilation.Diagnostics.All);

                var modules = compilation.Bind();
                output.WriteLine($"DateUtcHours stage=bind source={file}; count={compilation.Diagnostics.All.Count}");
                foreach (var diag in compilation.Diagnostics.All)
                {
                    var loc = diag.Location;
                    output.WriteLine($"severity={diag.Severity}; code={diag.Code}; numericCode={(int)diag.Code}; module={diag.ModuleId}; source={loc.Source}; line={loc.Line}; column={loc.Column}; offset={loc.Offset}; message={diag.Message}");
                }

                output.WriteLine($"DateUtcHours stage=hosting-build-start source={file}");
                await using var runtime = await BuildEpochAsync(dir);
                output.WriteLine("DateUtcHours stage=hosting-build-success");
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
                Assert.Equal("getUTCHours", method.Name);
                Assert.False(method.IsStatic);
                Assert.Empty(method.Parameters);
                Assert.Empty(call.Arguments);
                Assert.Equal(TsType.Number, method.Type);
                Assert.Equal(TsType.Number, call.Type);

                var receiver = Assert.IsType<TsClassType>(member.Object.Type);
                Assert.Equal("Date", receiver.Name);
                output.WriteLine($"DateUtcHours stage=bound receiver={receiver.Name}; owner={method.DeclaringClassName}; method={method.Name}; static={method.IsStatic}; explicitParameters={method.Parameters.Count}; explicitArgs={call.Arguments.Count}; methodResult={method.Type.Name}; callResult={call.Type.Name}");

                output.WriteLine("DateUtcHours stage=epoch-invoke-start");
                var hour = await runtime.InvokeAsync<double>("app", "date");
                LogNumber("epoch", "0", hour);
                Assert.Equal(0d, hour);
            });

        await WithAppAsync(
            "export function hour(text: string): number { const d = new Date(text); return d.getUTCHours(); }\n",
            async (dir, file) =>
            {
                output.WriteLine($"DateUtcHours stage=typed-hosting-build-start source={file}");
                await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
                output.WriteLine("DateUtcHours stage=typed-hosting-build-success");
                foreach (var (row, input, expected) in new[]
                {
                    ("midday", MiddayText, 12d),
                    ("offset", OffsetText, 23d),
                    ("midnight", MidnightText, 0d)
                })
                {
                    var actual = await runtime.InvokeAsync<double>("app", "hour", input);
                    LogNumber(row, input, actual);
                    Assert.Equal(expected, actual);
                }
            });

        var capability = StandardLibraryManifest.Find("Date");
        Assert.NotNull(capability);
        Assert.Equal(StandardCapabilityStatus.Supported, capability.Status);
        Assert.Contains("getUTCHours()", capability.Signature);
        Assert.Contains("getUTCHours returns the UTC hour (0 through 23), or NaN if invalid or unavailable in the supported calendar.", capability.Semantics);
        Assert.Contains(nameof(GetUtcHours_ReturnsHourOfStoredUtcInstant), capability.Tests);

        var declarations = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var dateInterface = Assert.Single(Regex.Matches(declarations, @"(?m)^interface Date[ \t]*\{([^}]*)\}"));
        Assert.Single(Regex.Matches(dateInterface.Groups[1].Value,
            @"(?m)^[ \t]*getUTCHours\(\): number;\r?$"));
        output.WriteLine("DateUtcHours stage=metadata-success manifest=Supported; declaration=Date.getUTCHours(): number");
    }

    private async Task<TypeSharpRuntime> BuildEpochAsync(string dir)
    {
        try
        {
            return await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine($"DateUtcHours stage=hosting-build-failure type={ex.GetType().FullName}");
            output.WriteLine(ex.Message);
            throw;
        }
    }

    private void LogNumber(string row, string input, double actual) =>
        output.WriteLine(FormattableString.Invariant($"DateUtcHours row={row}; input={input}; actual={actual:R}"));

    private static async Task WithAppAsync(string source, Func<string, string, Task> action)
    {
        var dir = Directory.CreateTempSubdirectory("date-utc-hours-").FullName;
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
