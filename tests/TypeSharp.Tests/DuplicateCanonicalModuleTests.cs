using System;
using System.IO;
using System.Linq;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Syntax.Diagnostics;
using Xunit;

namespace TypeSharp.Tests;

public sealed class DuplicateCanonicalModuleTests
{
    [Fact]
    public void FlatThenNestedReportsCanonicalModuleCollision() => AssertCollision(nestedFirst: false);

    [Fact]
    public void NestedThenFlatReportsCanonicalModuleCollision() => AssertCollision(nestedFirst: true);

    [Fact]
    public void RepeatedNormalizedPathAndDistinctModuleCompile()
    {
        var root = Directory.CreateTempSubdirectory("typesharp-duplicate-module-");
        try
        {
            var (ledger, nested) = WriteSources(root.FullName, "ledger.ts");
            var compilation = new TypeScriptCompilation(root.FullName);
            compilation.AddSourceFile(ledger);
            compilation.AddSourceFile(ledger);
            compilation.AddSourceFile(Path.Combine(root.FullName, ".", "ledger.ts"));
            compilation.AddSourceFile(nested);

            var modules = compilation.Compile();

            Assert.Empty(compilation.Diagnostics.All);
            Assert.Equal(2, modules.Count);
            Assert.Equal(ledger, modules["ledger"].SourcePath);
            Assert.Equal(nested, modules["billing"].SourcePath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void AssertCollision(bool nestedFirst)
    {
        var root = Directory.CreateTempSubdirectory("typesharp-duplicate-module-");
        try
        {
            var (flat, nested) = WriteSources(root.FullName, "billing.ts");
            var (first, second) = nestedFirst ? (nested, flat) : (flat, nested);
            Assert.Equal("billing", TypeScriptCompilation.ComputeModuleId(first, root.FullName));
            Assert.Equal("billing", TypeScriptCompilation.ComputeModuleId(second, root.FullName));
            var compilation = new TypeScriptCompilation(root.FullName);
            compilation.AddSourceFile(first);
            compilation.AddSourceFile(second);

            var modules = compilation.Compile();

            Assert.True(compilation.Diagnostics.ErrorCount == 1,
                $"Expected one duplicate-module error, observed {compilation.Diagnostics.ErrorCount} errors " +
                $"and {modules.Count} emitted modules: " +
                string.Join(", ", modules.Select(pair => $"{pair.Key}={pair.Value.SourcePath}")));
            var diagnostic = Assert.Single(compilation.Diagnostics.All);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(DiagnosticCode.TS3001, diagnostic.Code);
            Assert.Equal("billing", diagnostic.ModuleId);
            Assert.Equal($"Duplicate module ID 'billing': source '{second}' conflicts with '{first}'.",
                diagnostic.Message);
            Assert.Equal(second, diagnostic.Location.Source);
            Assert.Equal(1, diagnostic.Location.Line);
            Assert.Equal(1, diagnostic.Location.Column);
            Assert.Equal(0, diagnostic.Location.Offset);
            Assert.Empty(modules);
            Assert.Empty(compilation.CompiledModules);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static (string Flat, string Nested) WriteSources(string root, string flatName)
    {
        Directory.CreateDirectory(Path.Combine(root, "billing"));
        var flat = Path.Combine(root, flatName);
        var nested = Path.Combine(root, "billing", "main.ts");
        File.WriteAllText(flat, "function value(): int32 { return 11; }");
        File.WriteAllText(nested, "function value(): int32 { return 22; }");
        return (flat, nested);
    }
}
