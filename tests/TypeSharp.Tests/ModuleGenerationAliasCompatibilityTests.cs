using System.Globalization;
using TypeSharp.Hosting;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class ModuleGenerationAliasCompatibilityTests
{
    internal const string SourceA = "export function tick(): number { return 10; }\n";
    internal const string SourceB = "export function tick(): number { return 20; }\n";

    [Fact]
    public async Task InitializeGenerationKeepsLastActiveBindingForCoincidentNames()
    {
        var root = Directory.CreateTempSubdirectory("typesharp-generation-alias-");
        try
        {
            var a = root.CreateSubdirectory("root-a").FullName;
            var b = root.CreateSubdirectory("root-b").FullName;
            await File.WriteAllTextAsync(Path.Combine(a, "shared.ts"), SourceA);
            await File.WriteAllTextAsync(Path.Combine(b, "shared.ts"), SourceB);
            await ObserveAsync(a, b);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    internal static async Task ObserveAsync(string rootA, string rootB, Action<string, string>? row = null)
    {
        var pathA = Path.Combine(rootA, "shared.ts");
        var pathB = Path.Combine(rootB, "shared.ts");
        Assert.NotEqual(pathA, pathB);
        Assert.Equal(SourceA, await File.ReadAllTextAsync(pathA));
        Assert.Equal(SourceB, await File.ReadAllTextAsync(pathB));

        await using var originA = await new TypeSharpRuntimeBuilder().AddSourceDirectory(rootA).BuildAsync();
        await using var originB = await new TypeSharpRuntimeBuilder().AddSourceDirectory(rootB).BuildAsync();
        var a = originA.Modules.GetModule("shared");
        var b = originB.Modules.GetModule("shared");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Equal("shared", a.Name);
        Assert.Equal("shared", b.Name);
        Assert.True(a.IsActive && b.IsActive);
        Assert.Equal(pathA, a.FilePath);
        Assert.Equal(pathB, b.FilePath);
        Assert.Equal(pathA, originA.Modules.GetModulePath("shared"));
        Assert.Equal(pathB, originB.Modules.GetModulePath("shared"));
        row?.Invoke("origin-compiled-references-paths", "A,B");
        var originTickA = await originA.InvokeAsync<double>("shared", "tick");
        var originTickB = await originB.InvokeAsync<double>("shared", "tick");
        Assert.Equal(10d, originTickA);
        Assert.Equal(20d, originTickB);
        row?.Invoke("origin-qualified-a", originTickA.ToString(CultureInfo.InvariantCulture));
        row?.Invoke("origin-qualified-b", originTickB.ToString(CultureInfo.InvariantCulture));

        await using var target = await new TypeSharpRuntimeBuilder().BuildAsync();
        var registry = target.Modules;
        Assert.Empty(registry.Modules);
        row?.Invoke("target-initial-bindings", registry.Modules.Count.ToString(CultureInfo.InvariantCulture));
        registry.Register(pathA, a);
        registry.RegisterAlias("b-binding", b, pathB);
        registry.RegisterAlias("a-tail", a, pathA);

        string Label(TsModule module) => ReferenceEquals(module, a) ? "A"
            : ReferenceEquals(module, b) ? "B"
            : throw new InvalidOperationException("Unexpected module reference");
        string List(IEnumerable<TsModule> modules) => string.Join(',', modules.Select(Label));
        void CheckBinding(string key, TsModule module, string path, bool active)
        {
            Assert.Same(module, registry.Modules[key]);
            Assert.Same(active ? module : null, registry.GetModule(key));
            Assert.Equal(path, registry.GetModulePath(key));
        }

        Assert.Equal("A,B,A", List(registry.Modules.Values));
        row?.Invoke("target-raw-active-order", List(registry.Modules.Values));
        CheckBinding("shared", a, pathA, active: true);
        CheckBinding("b-binding", b, pathB, active: true);
        CheckBinding("a-tail", a, pathA, active: true);
        row?.Invoke("target-active-lookups-paths", "A,B,A");

        var first = registry.Modules.Values.Distinct<TsModule>(ReferenceEqualityComparer.Instance).ToArray();
        Assert.Equal("A,B", List(first));
        var alternative = new Dictionary<string, TsModule>(StringComparer.Ordinal);
        foreach (var module in first)
            alternative[module.Name] = module;
        Assert.Same(b, alternative["shared"]);
        row?.Invoke("unique-fold-counterfactual", Label(alternative["shared"]));
        row?.Invoke("target-public-active-list", List(registry.GetActiveModules()));

        void CheckGeneration(TsModule expected, double expectedTick, string prefix)
        {
            using (var lease = target.AcquireGeneration())
            {
                Assert.NotNull(lease);
                var selected = Assert.Single(lease.Generation.Modules);
                Assert.Equal("shared", selected.Key);
                Assert.Same(expected, selected.Value);
                row?.Invoke(prefix + "-selected-reference", Label(selected.Value));
            }

            var value = target.InvokeWithLease("tick");
            var tick = value switch
            {
                TsInt32Value n => n.Value,
                TsFloat32Value n => n.Value,
                TsFloat64Value n => n.Value,
                TsDecimalValue n => (double)n.Value,
                _ => throw new InvalidOperationException("Expected numeric tick; got " + value?.ValueType)
            };
            Assert.Equal(expectedTick, tick);
            row?.Invoke(prefix + "-numeric-tick", tick.ToString(CultureInfo.InvariantCulture));
        }

        target.InitializeGeneration();
        CheckGeneration(a, 10d, "target-active");

        Assert.Same(a, registry.DeactivateModule("a-tail"));
        Assert.True(!a.IsActive && b.IsActive);
        Assert.Equal("A,B,A", List(registry.Modules.Values));
        CheckBinding("shared", a, pathA, active: false);
        CheckBinding("b-binding", b, pathB, active: true);
        CheckBinding("a-tail", a, pathA, active: false);
        row?.Invoke("target-inactive-raw-order", List(registry.Modules.Values));
        row?.Invoke("target-inactive-lookups-paths", "<null>,B,<null>");
        Assert.Equal("B", List(registry.GetActiveModules()));
        row?.Invoke("target-public-inactive-list", List(registry.GetActiveModules()));

        target.InitializeGeneration();
        CheckGeneration(b, 20d, "target-inactive");
    }
}
