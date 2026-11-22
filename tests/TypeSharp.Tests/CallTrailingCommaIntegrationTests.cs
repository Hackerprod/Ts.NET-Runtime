using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TypeSharp.Hosting;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class CallTrailingCommaIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public Task TrailingComma_PreservesExactlyTwoArguments() =>
        ObserveAsync(nameof(TrailingComma_PreservesExactlyTwoArguments), "pack(4, 7,)", 47d,
            call => AssertLiteralArguments(call, "4", "7"));

    [Fact]
    public Task NoTrailingComma_PreservesExactlyTwoArguments() =>
        ObserveAsync(nameof(NoTrailingComma_PreservesExactlyTwoArguments), "pack(4, 7)", 47d,
            call => AssertLiteralArguments(call, "4", "7"));

    [Fact]
    public Task NestedCall_PreservesBothArgumentLists() =>
        ObserveAsync(nameof(NestedCall_PreservesBothArgumentLists), "pack(1, pack(0, 9))", 19d, call =>
        {
            AssertOrdinaryCall(call, "pack", 2);
            AssertInteger(call.Arguments[0], "1");
            var nested = Assert.IsType<CallExpressionSyntax>(call.Arguments[1]);
            Assert.NotSame(call, nested);
            AssertLiteralArguments(nested, "0", "9");
            return "pack argc=2 order=IntegerLiteral:1,pack(nested argc=2 order=IntegerLiteral:0,IntegerLiteral:9)";
        });

    [Fact]
    public Task EmptyCall_PreservesZeroArguments() =>
        ObserveAsync(nameof(EmptyCall_PreservesZeroArguments), "zero()", 8d, call =>
        {
            AssertOrdinaryCall(call, "zero", 0);
            return "zero argc=0 order=empty";
        });

    private async Task ObserveAsync(string row, string expression, double expected, Func<CallExpressionSyntax, string> assertShape)
    {
        var dir = Directory.CreateTempSubdirectory("typesharp-call-comma-");
        try
        {
            var path = Path.Combine(dir.FullName, "app.ts");
            var encoding = new UTF8Encoding(false);
            var source = "function pack(a: number, b: number): number { return a * 10 + b; }\n" +
                         "function zero(): number { return 8; }\n" +
                         $"export function observe(): number {{ return {expression}; }}\n";
            await File.WriteAllTextAsync(path, source, encoding);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(encoding.GetBytes(source), bytes);
            output.WriteLine($"C149_CALL row={row} stage=source path={path} sha256={Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()} expression={expression}");

            output.WriteLine($"C149_CALL row={row} stage=hosting-build-start");
            TypeSharpRuntime runtime;
            try
            {
                runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(dir.FullName).BuildAsync();
            }
            catch (Exception error)
            {
                output.WriteLine($"C149_CALL row={row} stage=hosting-build-exception type={error.GetType().FullName}\n{error.Message}");
                throw;
            }

            await using (runtime)
            {
                output.WriteLine($"C149_CALL row={row} stage=hosting-build-success");
                var actual = await runtime.InvokeAsync<double>("app", "observe");
                output.WriteLine($"C149_CALL row={row} stage=hosting-invoke-success");
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"C149_CALL row={row} stage=value expected={expected:R} actual={actual:R}"));
                Assert.Equal(expected, actual);

                var tokens = new Lexer(encoding.GetString(bytes), path).Tokenize();
                Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
                var parser = new Parser(tokens);
                var parsed = parser.Parse(path);
                Assert.Empty(parser.Diagnostics);
                var observe = Assert.Single(parsed.Members.OfType<FunctionDeclarationSyntax>(), fn => fn.Name == "observe");
                var body = Assert.IsType<BlockStatementSyntax>(observe.Body);
                var result = Assert.IsType<ReturnStatementSyntax>(Assert.Single(body.Statements));
                var call = Assert.IsType<CallExpressionSyntax>(result.Value);
                output.WriteLine($"C149_CALL row={row} stage=ast callee={assertShape(call)}");
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void AssertOrdinaryCall(CallExpressionSyntax call, string callee, int count)
    {
        Assert.False(call.IsNullConditional);
        Assert.Empty(call.TypeArguments);
        Assert.Equal(callee, Assert.IsType<IdentifierExpressionSyntax>(call.Callee).Name);
        Assert.Equal(count, call.Arguments.Count);
    }

    private static void AssertInteger(ExpressionSyntax expression, string text)
    {
        var literal = Assert.IsType<LiteralExpressionSyntax>(expression);
        Assert.Equal(TokenKind.IntegerLiteral, literal.Token.Kind);
        Assert.Equal(text, literal.Token.Text);
    }

    private static string AssertLiteralArguments(CallExpressionSyntax call, string first, string second)
    {
        AssertOrdinaryCall(call, "pack", 2);
        AssertInteger(call.Arguments[0], first);
        AssertInteger(call.Arguments[1], second);
        return $"pack argc=2 order=IntegerLiteral:{first},IntegerLiteral:{second}";
    }
}
