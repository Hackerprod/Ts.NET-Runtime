using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TypeSharp.Hosting;
using TypeSharp.Hosting.Compilation;
using Xunit;

namespace TypeSharp.Tests;

public sealed class InitialSourceIntakeTests
{
    private const string LowerCaseSource = "export function tick(): number { return 41; }";
    private const string UpperCaseSource = "export function tick(): number { return 42; }";
    private const string MixedPeer = "export function tick(): number { return 22; }";

    [Fact]
    public void DirectDirectoryDiscoversMixedSuffixAndExcludesOtherFiles()
    {
        using var root = new TempRoot();
        WriteMixedDirectory(root.Path);
        var compilation = new TypeScriptCompilation(root.Path);
        compilation.AddSourceDirectory(System.IO.Path.Combine(root.Path, "absent"));
        compilation.AddSourceDirectory(root.Path);
        var modules = compilation.Compile();

        Assert.Empty(compilation.Diagnostics.All);
        Assert.Equal("lower,nested,nested/Peer", ModuleSet(modules.Keys));
        Assert.Equal(System.IO.Path.Combine(root.Path, "nested", "Peer.Ts"), modules["nested/Peer"].SourcePath);
    }

    [Fact]
    public async Task HostedDirectoryDiscoversMixedSuffixAndInvokesEveryModule()
    {
        using var root = new TempRoot();
        WriteMixedDirectory(root.Path);
        await using var host = await new TypeSharpRuntimeBuilder().AddSourceDirectory(root.Path).BuildAsync();

        Assert.Equal("lower,nested,nested/Peer", ModuleSet(host.ActiveGeneration!.Modules.Keys));
        Assert.Equal(11, await host.InvokeAsync<int>("lower", "tick"));
        Assert.Equal(33, await host.InvokeAsync<int>("nested", "tick"));
        Assert.Equal(22, await host.InvokeAsync<int>("nested/Peer", "tick"));
    }

    [Fact]
    public async Task HostedExplicitAcceptsMixedSuffixAndNormalizedDuplicates()
    {
        using var root = new TempRoot();
        WriteMixedDirectory(root.Path);
        string lower = System.IO.Path.Combine(root.Path, "lower.ts");
        string nested = System.IO.Path.Combine(root.Path, "nested");
        await using var host = await new TypeSharpRuntimeBuilder()
            .AddSourceFile(lower)
            .AddSourceFile(lower)
            .AddSourceFile(System.IO.Path.Combine(root.Path, ".", "lower.ts"))
            .AddSourceFile(System.IO.Path.Combine(nested, "main.ts"))
            .AddSourceFile(System.IO.Path.Combine(nested, "Peer.Ts"))
            .AddSourceFile(System.IO.Path.Combine(root.Path, "ambient.d.ts"))
            .AddSourceFile(System.IO.Path.Combine(root.Path, "other.d.TS"))
            .AddSourceFile(System.IO.Path.Combine(root.Path, "other.txt"))
            .BuildAsync();

        Assert.Equal("lower,nested,nested/Peer", ModuleSet(host.ActiveGeneration!.Modules.Keys));
        Assert.Equal(11, await host.InvokeAsync<int>("lower", "tick"));
        Assert.Equal(33, await host.InvokeAsync<int>("nested", "tick"));
        Assert.Equal(22, await host.InvokeAsync<int>("nested/Peer", "tick"));
    }

    [LinuxCaseDistinctFact]
    public async Task HostedExplicitPreservesPhysicallyDistinctCasePeers()
    {
        using var root = new TempRoot();
        var (lower, upper) = RequireCasePair(root.Path);
        await using var host = await new TypeSharpRuntimeBuilder()
            .AddSourceFile(lower)
            .AddSourceFile(lower)
            .AddSourceFile(System.IO.Path.Combine(root.Path, ".", "item.ts"))
            .AddSourceFile(upper)
            .BuildAsync();

        await AssertCasePair(host);
    }

    [LinuxCaseDistinctFact]
    public async Task HostedDirectoryPreservesPhysicallyDistinctCasePeers()
    {
        using var root = new TempRoot();
        RequireCasePair(root.Path);
        await using var host = await new TypeSharpRuntimeBuilder().AddSourceDirectory(root.Path).BuildAsync();

        await AssertCasePair(host);
    }

    [Fact]
    public async Task RepeatedExplicitMainKeepsCountSensitiveRoot()
    {
        using var root = new TempRoot();
        var child = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "nested")).FullName;
        var main = System.IO.Path.Combine(child, "main.ts");
        File.WriteAllText(main, MixedPeer);
        Assert.Equal(root.Path, TypeSharpRuntimeBuilder.DetermineSourceRoot(new[] { main }, Array.Empty<string>()));
        Assert.Equal(child, TypeSharpRuntimeBuilder.DetermineSourceRoot(new[] { main, main }, Array.Empty<string>()));
        await using var single = await new TypeSharpRuntimeBuilder().AddSourceFile(main).BuildAsync();
        await using var repeated = await new TypeSharpRuntimeBuilder()
            .AddSourceFile(main).AddSourceFile(main)
            .AddSourceFile(System.IO.Path.Combine(child, ".", "main.ts")).BuildAsync();

        Assert.Equal("nested", ModuleSet(single.ActiveGeneration!.Modules.Keys));
        Assert.Equal(22, await single.InvokeAsync<int>("nested", "tick"));
        Assert.Equal("nested", ModuleSet(repeated.ActiveGeneration!.Modules.Keys));
        Assert.Equal(22, await repeated.InvokeAsync<int>("nested", "tick"));
    }

    private static async Task AssertCasePair(TypeSharpRuntime host)
    {
        Assert.Equal("Item,item", ModuleSet(host.ActiveGeneration!.Modules.Keys));
        Assert.Equal(41, await host.InvokeAsync<int>("item", "tick"));
        Assert.Equal(42, await host.InvokeAsync<int>("Item", "tick"));
    }

    private static string ModuleSet(System.Collections.Generic.IEnumerable<string> modules) =>
        string.Join(",", modules.OrderBy(id => id, StringComparer.Ordinal));

    private static void WriteMixedDirectory(string root)
    {
        var nested = Directory.CreateDirectory(System.IO.Path.Combine(root, "nested")).FullName;
        File.WriteAllText(System.IO.Path.Combine(root, "lower.ts"), "export function tick(): number { return 11; }");
        File.WriteAllText(System.IO.Path.Combine(nested, "main.ts"), "export function tick(): number { return 33; }");
        File.WriteAllText(System.IO.Path.Combine(nested, "Peer.Ts"), MixedPeer);
        File.WriteAllText(System.IO.Path.Combine(root, "ambient.d.ts"), "declare const ambient: number;");
        File.WriteAllText(System.IO.Path.Combine(root, "other.d.TS"), "declare const other: number;");
        File.WriteAllText(System.IO.Path.Combine(root, "other.txt"), "plain text");
    }

    private static (string Lower, string Upper) RequireCasePair(string root)
    {
        Assert.True(TryWriteCasePair(root, out var lower, out var upper),
            "The filesystem used by the test differs from the capability check.");
        return (lower, upper);
    }

    private static bool TryWriteCasePair(string root, out string lower, out string upper)
    {
        lower = System.IO.Path.Combine(root, "item.ts");
        upper = System.IO.Path.Combine(root, "Item.ts");
        var lowerBytes = Encoding.UTF8.GetBytes(LowerCaseSource);
        var upperBytes = Encoding.UTF8.GetBytes(UpperCaseSource);
        WriteNew(lower, lowerBytes);
        try
        {
            WriteNew(upper, upperBytes);
        }
        catch (IOException) when (
            Directory.GetFiles(root).SequenceEqual(new[] { lower }, StringComparer.Ordinal)
            && File.Exists(upper)
            && File.ReadAllBytes(upper).SequenceEqual(lowerBytes))
        {
            return false; // Exclusive create found the one existing disk file through an alias.
        }

        Assert.Equal("Item.ts,item.ts", ModuleSet(Directory.GetFiles(root).Select(path => System.IO.Path.GetFileName(path))));
        Assert.Equal(lowerBytes, File.ReadAllBytes(lower));
        Assert.Equal(upperBytes, File.ReadAllBytes(upper));
        return true;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(bytes);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("typesharp-intake-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    // xUnit v2 uses the Fact.Skip discovery property; a skipped case is not a pass.
    public sealed class LinuxCaseDistinctFactAttribute : FactAttribute
    {
        private static readonly Lazy<string?> SkipReason = new(() =>
        {
            if (!OperatingSystem.IsLinux())
                return "Linux-specific intake behavior.";
            using var root = new TempRoot();
            return TryWriteCasePair(root.Path, out _, out _)
                ? null : "Temporary filesystem aliases the two filename spellings.";
        });

        public LinuxCaseDistinctFactAttribute() => Skip = SkipReason.Value;
    }
}
