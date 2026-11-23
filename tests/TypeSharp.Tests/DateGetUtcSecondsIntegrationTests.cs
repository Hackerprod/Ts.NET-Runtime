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

public sealed class DateGetUtcSecondsIntegrationTests(ITestOutputHelper output)
{
    private const string MiddayText = "2024-02-25T12:15:37.456Z";
    private const string LastText = "2000-01-01T00:00:59.999Z";
    private const string OffsetText = "2000-01-01T00:10:12+05:45";

    [Fact]
    public async Task GetUtcSeconds_ReturnsSecondOfStoredUtcInstant()
    {
        await WithAppAsync(
            "export function date(): any { const date = new Date(0); return date.getUTCSeconds(); }\n",
            async (dir, file) =>
            {
                var compilation = new TypeScriptCompilation(dir);
                compilation.AddSourceFile(file);
                compilation.Parse();
                output.WriteLine($"C166 DateUtcSeconds stage=parse count={compilation.Diagnostics.All.Count}");
                Assert.Empty(compilation.Diagnostics.All);

                var modules = compilation.Bind();
                output.WriteLine($"C166 DateUtcSeconds stage=bind count={compilation.Diagnostics.All.Count}");
                foreach (var diag in compilation.Diagnostics.All)
                {
                    var loc = diag.Location;
                    output.WriteLine($"C166 DateUtcSeconds stage=bind-diagnostic severity={diag.Severity}; code={diag.Code}; numericCode={(int)diag.Code}; module={diag.ModuleId}; source={loc.Source}; line={loc.Line}; column={loc.Column}; offset={loc.Offset}; message={diag.Message}");
                }

                output.WriteLine("C166 DateUtcSeconds stage=hosting-build-start");
                await using var runtime = await BuildEpochAsync(dir);
                output.WriteLine("C166 DateUtcSeconds stage=hosting-build-success");
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
                Assert.Equal("getUTCSeconds", method.Name);
                Assert.False(method.IsStatic);
                Assert.Empty(method.Parameters);
                Assert.Empty(call.Arguments);
                Assert.Equal(TsType.Number, method.Type);
                Assert.Equal(TsType.Number, call.Type);

                var receiver = Assert.IsType<TsClassType>(member.Object.Type);
                Assert.Equal("Date", receiver.Name);
                output.WriteLine($"C166 DateUtcSeconds stage=bound receiver={receiver.Name}; owner={method.DeclaringClassName}; method={method.Name}; static={method.IsStatic}; explicitParameters={method.Parameters.Count}; explicitArgs={call.Arguments.Count}; methodResult={method.Type.Name}; callResult={call.Type.Name}");

                var second = await runtime.InvokeAsync<double>("app", "date");
                LogNumber("epoch", "0", second);
                Assert.Equal(0d, second);
            });

        await WithAppAsync(
            "export function unix(ms: number): number { const d = new Date(ms); return d.getUTCSeconds(); }\nexport function iso(text: string): number { const d = new Date(text); return d.getUTCSeconds(); }\n",
            async (dir, _) =>
            {
                output.WriteLine("C166 DateUtcSeconds stage=typed-hosting-build-start");
                await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
                output.WriteLine("C166 DateUtcSeconds stage=typed-hosting-build-success");
                var beforeEpoch = await runtime.InvokeAsync<double>("app", "unix", -1d);
                LogNumber("before-epoch", "-1", beforeEpoch);
                Assert.Equal(59d, beforeEpoch);

                foreach (var (row, input, expected) in new[]
                {
                    ("midday", MiddayText, 37d),
                    ("offset", OffsetText, 12d),
                    ("last", LastText, 59d),
                    ("invalid", "not-a-date", double.NaN)
                })
                {
                    var actual = await runtime.InvokeAsync<double>("app", "iso", input);
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
        Assert.Single(Regex.Matches(capability.Signature, @"(?<![A-Za-z0-9_$])getUTCSeconds\(\)(?![A-Za-z0-9_$])"));
        Assert.Contains("getUTCSeconds returns the UTC second (0 through 59), or NaN if invalid or unavailable in the supported calendar.", capability.Semantics);
        Assert.Contains(nameof(GetUtcSeconds_ReturnsSecondOfStoredUtcInstant), capability.Tests);

        var declarations = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "templates", "standard-ts", "types", "typesharp-runtime.d.ts"));
        var dateInterface = Assert.Single(Regex.Matches(declarations, @"(?m)^interface Date[ \t]*\{([^}]*)\}"));
        Assert.Single(Regex.Matches(dateInterface.Groups[1].Value,
            @"(?m)^[ \t]*getUTCSeconds\(\): number;\r?$"));
        output.WriteLine("C166 DateUtcSeconds stage=metadata-success manifest=Supported; declaration=Date.getUTCSeconds(): number");
    }

    private async Task<TypeSharpRuntime> BuildEpochAsync(string dir)
    {
        try
        {
            return await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir).BuildAsync();
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine($"C166 DateUtcSeconds stage=hosting-build-failure type={ex.GetType().FullName}");
            output.WriteLine($"C166 DateUtcSeconds stage=hosting-build-message {ex.Message}");
            throw;
        }
    }

    private void LogNumber(string row, string input, double actual) =>
        output.WriteLine(FormattableString.Invariant($"C166 DateUtcSeconds row={row}; input={input}; actual={actual:R}"));

    private static async Task WithAppAsync(string source, Func<string, string, Task> action)
    {
        var dir = Directory.CreateTempSubdirectory("date-utc-seconds-").FullName;
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
