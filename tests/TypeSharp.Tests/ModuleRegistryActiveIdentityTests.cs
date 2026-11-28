using TypeSharp.Hosting;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Bytecode;
using Xunit;

namespace TypeSharp.Tests;

public sealed class ModuleRegistryActiveIdentityTests
{
    [Fact]
    public void ActiveListingUsesFirstReferenceAndKeepsSnapshotMembership()
    {
        var registry = new ModuleRegistry();
        var sharedCode = new BytecodeModule("z", []);
        var z = new TsModule("z", "shared.ts", sharedCode, 7);
        var x = new TsModule("x", "shared.ts", sharedCode, 7);
        var aliasOnly = new TsModule("unlisted", "shared.ts", sharedCode, 7);

        registry.Register("shared.ts", z);
        registry.RegisterAlias("z-first", z, "first.ts");
        registry.Register("shared.ts", x);
        registry.RegisterAlias("z-tail", z, "tail.ts");
        registry.RegisterAlias("alias-only", aliasOnly, "alias-only.ts");
        var snapshot = registry.GetActiveModules();

        Assert.Same(z, registry.GetModule("z"));
        Assert.Same(z, registry.GetModule("z-first"));
        Assert.Equal("first.ts", registry.GetModulePath("z-first"));
        Assert.Same(z, registry.GetModule("z-tail"));
        Assert.Equal("tail.ts", registry.GetModulePath("z-tail"));
        Assert.Same(aliasOnly, registry.GetModule("alias-only"));
        Assert.Null(registry.GetModule("unlisted"));
        Assert.Equal("alias-only.ts", registry.GetModulePath("alias-only"));

        Assert.Same(z, registry.DeactivateModule("z-tail"));
        Assert.Null(registry.GetModule("z"));
        Assert.Null(registry.GetModule("z-first"));
        Assert.Null(registry.GetModule("z-tail"));
        Assert.Equal("shared.ts", registry.GetModulePath("z"));
        Assert.Equal("first.ts", registry.GetModulePath("z-first"));
        Assert.Equal("tail.ts", registry.GetModulePath("z-tail"));
        var inactive = registry.GetActiveModules();
        z.IsActive = true;
        var reactivated = registry.GetActiveModules();

        registry.RemoveModule("z-first");
        Assert.Null(registry.GetModule("z-first"));
        Assert.Null(registry.GetModulePath("z-first"));
        Assert.Same(z, registry.GetModule("z-tail"));
        var afterAliasRemoval = registry.GetActiveModules();

        var replacement = new TsModule("z", "shared.ts", sharedCode, 7);
        registry.Register(replacement.FilePath, replacement);
        Assert.Same(replacement, registry.GetModule("z"));
        Assert.Same(z, registry.GetModule("z-tail"));
        Assert.Equal("tail.ts", registry.GetModulePath("z-tail"));
        var afterReplacement = registry.GetActiveModules();
        registry.RemoveModule("z");
        Assert.Null(registry.GetModule("z"));
        Assert.Null(registry.GetModulePath("z"));
        Assert.Same(z, registry.GetModule("z-tail"));
        var afterCanonicalRemoval = registry.GetActiveModules();

        var mismatches = new List<string>();
        void Check(string label, IReadOnlyList<TsModule> actual, params TsModule[] expected)
        {
            if (!actual.SequenceEqual(expected, ReferenceEqualityComparer.Instance))
                mismatches.Add(label + "=" + string.Join(',', actual.Select(m =>
                    ReferenceEquals(m, z) ? "old-z" : ReferenceEquals(m, replacement) ? "new-z" : m.Name)));
        }

        Check("snapshot", snapshot, z, x, aliasOnly);
        Check("inactive", inactive, x, aliasOnly);
        Check("reactivated", reactivated, z, x, aliasOnly);
        Check("after-alias-removal", afterAliasRemoval, z, x, aliasOnly);
        Check("after-replacement", afterReplacement, replacement, x, z, aliasOnly);
        Check("after-canonical-removal", afterCanonicalRemoval, x, z, aliasOnly);
        Assert.True(mismatches.Count == 0, "registry-listing: " + string.Join("; ", mismatches));
    }

    private static string LegacyAlias(string path) =>
        Path.ChangeExtension(Path.GetRelativePath(Directory.GetCurrentDirectory(), path), null)!
            .Replace('\\', '/').TrimStart('/');

    [Fact]
    public async Task SourceDirectoryAliasesDoNotDuplicateActiveObjects()
    {
        var root = Directory.CreateTempSubdirectory("typesharp-active-registry-");
        try
        {
            var pathA = Path.Combine(root.FullName, "modA.ts");
            var pathB = Path.Combine(root.FullName, "modB.ts");
            await File.WriteAllTextAsync(pathA, "export function tick(): number { return 10; }");
            await File.WriteAllTextAsync(pathB, "export function tick(): number { return 20; }");
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(root.FullName).BuildAsync();
            var registry = runtime.Modules;
            var a = registry.GetModule("modA");
            var b = registry.GetModule("modB");
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotSame(a, b);
            Assert.Equal(4, registry.Modules.Count);
            Assert.Same(a, registry.GetModule(LegacyAlias(pathA)));
            Assert.Same(b, registry.GetModule(LegacyAlias(pathB)));
            Assert.Equal(pathA, registry.GetModulePath("modA"));
            Assert.Equal(pathA, registry.GetModulePath(LegacyAlias(pathA)));
            Assert.Equal(pathB, registry.GetModulePath("modB"));
            Assert.Equal(pathB, registry.GetModulePath(LegacyAlias(pathB)));
            Assert.Equal(10, await runtime.InvokeAsync<int>("modA", "tick"));
            Assert.Equal(20, await runtime.InvokeAsync<int>("modB", "tick"));

            var actual = registry.GetActiveModules();
            var expected = registry.Modules.Values.Distinct<TsModule>(ReferenceEqualityComparer.Instance);
            Assert.True(actual.Count == 2 && actual.SequenceEqual(expected, ReferenceEqualityComparer.Instance),
                "hosting-listing: " + string.Join(',', actual.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
