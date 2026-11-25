using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.Parser;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class ConstMissingInitializerIntegrationTests(ITestOutputHelper output)
{
    private const string Marker = "C208_CONST_MISSING_INITIALIZER";
    private const string MissingInitializerMessage = "'const' declarations must be initialized";
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void Compilation_RejectsOrdinaryConstWithoutInitializer()
    {
        var mismatches = new List<string>();
        Observe("local", "export function observe(): number { const value: number; return 9; }", 43);
        Observe("list", "const first: number = 2, missing: number; export function observe(): number { return 9; }", 26);
        Observe("classic-for", "export function observe(): number { for (const k: number; false;) { } return 9; }", 48);

        Observe("initialized-local", "export function observe(): number { const value: number = 7; return value; }",
            includeDeclarationFile: true);
        Observe("assigned-let", "export function observe(): number { let value: number; value = 7; return value; }");
        Observe("initialized-list", "export function observe(): number { const first: number = 2, second: number = 7; return second; }");
        Observe("for-of", "export function observe(): number { for (const value of [4, 5]) { } return 9; }",
            iterationKind: nameof(ForOfStatementSyntax));
        Observe("for-in", "export function observe(): number { for (const key in { first: 1, second: 2 }) { } return 9; }",
            iterationKind: nameof(ForInStatementSyntax));

        Assert.True(mismatches.Count == 0,
            $"C208_CONST_MISSING_INITIALIZER_MISMATCH {string.Join("; ", mismatches)}");

        void Observe(string row, string source, int? diagnosticColumn = null,
            bool includeDeclarationFile = false, string? iterationKind = null)
        {
            var directory = Directory.CreateTempSubdirectory("typesharp-c208-");
            try
            {
                var path = Path.Combine(directory.FullName, "app.ts");
                var bytes = WriteSource(row, path, source, mismatches);
                var compilation = new TypeScriptCompilation(directory.FullName);
                compilation.AddSourceFile(path);
                if (includeDeclarationFile)
                {
                    var declarationPath = Path.Combine(directory.FullName, "ambient.d.ts");
                    WriteSource(row, declarationPath, "declare const value: number;", mismatches);
                    compilation.AddSourceFile(declarationPath);
                    output.WriteLine($"{Marker} row={row} stage=register-declaration path={declarationPath}");
                }

                var modules = compilation.Compile().Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var diagnostics = compilation.Diagnostics.All;
                var hasErrors = compilation.Diagnostics.HasErrors;
                output.WriteLine(FormattableString.Invariant(
                    $"{Marker} row={row} stage=compile diagnostics={diagnostics.Count} hasErrors={hasErrors} modules=[{string.Join(",", modules)}]"));
                foreach (var diagnostic in diagnostics)
                    LogDiagnostic(row, "compile-diagnostic", diagnostic);

                if (diagnosticColumn is int column)
                {
                    if (!hasErrors || modules.Length != 0 || diagnostics.Count != 1 ||
                        !MatchesMissingInitializer(diagnostics[0], path, column))
                        mismatches.Add($"{row}: expected one positioned missing-initializer error and no modules");
                }
                else if (hasErrors || diagnostics.Count != 0 || modules.Length != 1 || modules[0] != "app")
                {
                    mismatches.Add($"{row}: expected no diagnostics and module app");
                }

                if (iterationKind is not null)
                {
                    if (!ObserveIteration(row, path, bytes, iterationKind))
                        mismatches.Add($"{row}: expected a clean const {iterationKind} parser node");
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private byte[] WriteSource(string row, string path, string source, List<string> mismatches)
    {
        var expected = Encoding.GetBytes(source + "\n");
        File.WriteAllBytes(path, expected);
        var actual = File.ReadAllBytes(path);
        output.WriteLine(FormattableString.Invariant(
            $"{Marker} row={row} stage=source path={path} bytes={actual.Length} sha256={Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant()}"));
        if (!actual.SequenceEqual(expected))
            mismatches.Add($"{row}: unexpected source bytes in {Path.GetFileName(path)}");
        return actual;
    }

    private bool ObserveIteration(string row, string path, byte[] source, string expectedKind)
    {
        var tokens = new Lexer(Encoding.GetString(source), path).Tokenize();
        var tokenErrors = tokens.Where(token => token.Kind == TokenKind.Error).ToArray();
        var parser = new Parser(tokens);
        var tree = parser.Parse(path);
        var body = (tree.Members.Count == 1 ? tree.Members[0] as FunctionDeclarationSyntax : null)
            ?.Body as BlockStatementSyntax;
        var first = body?.Statements.FirstOrDefault();
        bool? isConst = first switch
        {
            ForOfStatementSyntax forOf => forOf.IsConst,
            ForInStatementSyntax forIn => forIn.IsConst,
            _ => null
        };
        var kind = first?.GetType().Name;
        output.WriteLine(FormattableString.Invariant(
            $"{Marker} row={row} stage=iteration-parser tokenErrors={tokenErrors.Length} diagnostics={parser.Diagnostics.Count} kind={kind ?? "<none>"} isConst={isConst?.ToString() ?? "<none>"}"));
        foreach (var diagnostic in parser.Diagnostics)
            LogDiagnostic(row, "iteration-diagnostic", diagnostic);
        return tokenErrors.Length == 0 && parser.Diagnostics.Count == 0 && kind == expectedKind && isConst == true;
    }

    private void LogDiagnostic(string row, string stage, Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        output.WriteLine(FormattableString.Invariant(
            $"{Marker} row={row} stage={stage} severity={diagnostic.Severity} code={diagnostic.Code} module={diagnostic.ModuleId ?? "<none>"} source={location.Source} line={location.Line} column={location.Column} offset={location.Offset} message={diagnostic.Message}"));
    }

    private static bool MatchesMissingInitializer(Diagnostic diagnostic, string path, int column) =>
        diagnostic.Severity == DiagnosticSeverity.Error &&
        diagnostic.Code == DiagnosticCode.None &&
        diagnostic.Message == MissingInitializerMessage &&
        diagnostic.ModuleId == "app" &&
        diagnostic.Location.Source == path &&
        diagnostic.Location.Line == 1 &&
        diagnostic.Location.Column == column &&
        diagnostic.Location.Offset == column - 1;
}
