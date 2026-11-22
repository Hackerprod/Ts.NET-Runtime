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

public sealed class DateGetUtcFullYearIntegrationTests(ITestOutputHelper output)
{
    private const string OffsetText = "2000-01-01T00:30:00+01:00";

    [Fact]
    public async Task GetUtcFullYear_ReturnsNativeUtcYearAndInvalidNaN()
    {
        await WithAppAsync(
            "export function year(): any { const date = new Date(0); return date.getUTCFullYear(); }\n",
            async (dir, file) =>
            {
                var compilation = new TypeScriptCompilation(dir);
                compilation.AddSourceFile(file);
                compilation.Parse();
                output.WriteLine($"DateUtcYear stage=parse source={file}; count={compilation.Diagnostics.All.Count}");
                Assert.Empty(compilation.Diagnostics.All);

                var modules = compilation.Bind();
                output.WriteLine($"DateUtcYear stage=bind source={file}; count={compilation.Diagnostics.All.Count}");
                foreach (var diag in compilation.Diagnostics.All)
                {
                    var loc = diag.Location;
                    output.WriteLine($"severity={diag.Severity}; code={diag.Code}; numericCode={(int)diag.Code}; module={diag.ModuleId}; source={loc.Source}; line={loc.Line}; column={loc.Column}; offset={loc.Offset}; message={diag.Message}");
                }

                output.WriteLine($"DateUtcYear stage=hosting-build-start source={file}");
                await using var runtime = await BuildEpochAsync(dir);
                output.WriteLine("DateUtcYear stage=hosting-build-success");
                Assert.Empty(compilation.Diagnostics.All);
                var module = Assert.Single(modules);
                Assert.Equal("app", module.Key);
                var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(module.Value.Members));
                Assert.Equal("year", function.Symbol.Name);
                var body = Assert.IsType<BoundBlockStatement>(function.Body);
                var ret = Assert.Single(body.Statements.OfType<BoundReturnStatement>());
                var call = Assert.IsType<BoundCallExpression>(ret.Value);
                var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
                var method = Assert.IsType<MethodSymbol>(member.Member);
                Assert.Equal("Date", method.DeclaringClassName);
                Assert.Equal("getUTCFullYear", method.Name);
                Assert.False(method.IsStatic);
                Assert.Empty(method.Parameters);
                Assert.Empty(call.Arguments);
                Assert.Equal(TsType.Number, method.Type);
                Assert.Equal(TsType.Number, call.Type);

                var year = await runtime.InvokeAsync<double>("app", "year");
                LogNumber("epoch", year);
                Assert.Equal(1970d, year);
            });

        await WithAppAsync(
            "export function yearFromText(text: string): number { const date = new Date(text); return date.getUTCFullYear(); }\n",
            async (dir, _) =>
            {
                await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
                foreach (var (row, input, expected) in new[]
                {
                    ("offset", OffsetText, 1999d),
                    ("canonical-z", "2000-01-01T00:00:00.000Z", 2000d),
                    ("invalid", "not-a-date", double.NaN),
                    ("offset-repeat", OffsetText, 1999d)
                })
                {
                    var actual = await runtime.InvokeAsync<double>("app", "yearFromText", input);
                    LogNumber(row, actual);
                    if (double.IsNaN(expected))
                        Assert.True(double.IsNaN(actual));
                    else
                        Assert.Equal(expected, actual);
                }
            });

        var capability = StandardLibraryManifest.Find("Date");
        Assert.NotNull(capability);
        Assert.Equal(StandardCapabilityStatus.Supported, capability.Status);
        Assert.Contains("getUTCFullYear", capability.Signature);
        Assert.Contains("getUTCFullYear returns the UTC year or NaN", capability.Semantics);
        Assert.Contains(nameof(GetUtcFullYear_ReturnsNativeUtcYearAndInvalidNaN), capability.Tests);

        var declarations = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var dateInterface = Assert.Single(Regex.Matches(declarations, @"(?m)^interface Date[ \t]*\{([^}]*)\}"));
        Assert.Single(Regex.Matches(dateInterface.Groups[1].Value,
            @"(?m)^[ \t]*getUTCFullYear\(\): number;\r?$"));
    }

    [Fact]
    public async Task ExistingDateMethods_ReturnUtcValues()
    {
        await WithAppAsync("""
            export function epoch(): number { return new Date(0).getTime(); }
            export function time(text: string): number { return new Date(text).getTime(); }
            export function value(text: string): number { return new Date(text).valueOf(); }
            export function iso(text: string): string { return new Date(text).toISOString(); }
            """, async (dir, _) =>
        {
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
            var epoch = await runtime.InvokeAsync<double>("app", "epoch");
            LogNumber("control-epoch", epoch);
            Assert.Equal(0d, epoch);
            var time = await runtime.InvokeAsync<double>("app", "time", OffsetText);
            LogNumber("control-offset-time", time);
            Assert.Equal(946683000000d, time);
            var value = await runtime.InvokeAsync<double>("app", "value", OffsetText);
            LogNumber("control-offset-value", value);
            Assert.Equal(946683000000d, value);
            var iso = await runtime.InvokeAsync<string>("app", "iso", OffsetText);
            output.WriteLine($"DateUtcYear row=control-offset-iso; actual={iso}");
            Assert.Equal("1999-12-31T23:30:00.000Z", iso);
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
            output.WriteLine($"DateUtcYear stage=hosting-build-failure type={ex.GetType().FullName}");
            output.WriteLine(ex.Message);
            throw;
        }
    }

    private void LogNumber(string row, double actual) =>
        output.WriteLine(FormattableString.Invariant($"DateUtcYear row={row}; actual={actual:R}"));

    private static async Task WithAppAsync(string source, Func<string, string, Task> action)
    {
        var dir = Directory.CreateTempSubdirectory("date-utc-year-").FullName;
        try
        {
            var file = Path.Combine(dir, "app.ts");
            await File.WriteAllTextAsync(file, source);
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
