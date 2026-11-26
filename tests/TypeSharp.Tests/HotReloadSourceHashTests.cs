using System;
using System.IO;
using TypeSharp.Runtime.Generations;
using Xunit;

namespace TypeSharp.Tests;

public sealed class HotReloadSourceHashTests
{
    private const string Source = "export const n = 1;";

    private static (string Relative, string Full) Paths()
    {
        string relative = Path.Combine("hot-reload-test", ".", "entry.ts");
        string full = Path.GetFullPath(relative);
        Assert.False(string.Equals(relative, full, StringComparison.Ordinal));
        Assert.Equal(full, Path.GetFullPath(Path.Combine("hot-reload-test", "entry.ts")));
        return (relative, full);
    }

    [Fact]
    public void FullCommitRelativeLookup()
    {
        var (relative, full) = Paths();
        var manager = new HotReloadManager();
        manager.CommitSourceHash(full, Source);
        Assert.False(manager.HasChanges(relative, Source), "full-commit-relative-lookup: expected False; actual True");
    }

    [Fact]
    public void RelativeCommitFullLookup()
    {
        var (relative, full) = Paths();
        var manager = new HotReloadManager();
        manager.CommitSourceHash(relative, Source);
        Assert.False(manager.HasChanges(full, Source), "relative-commit-full-lookup: expected False; actual True");
    }

    [Fact]
    public void CanonicalUnchanged()
    {
        var (_, full) = Paths();
        var manager = new HotReloadManager();
        manager.CommitSourceHash(full, Source);
        Assert.False(manager.HasChanges(full, Source));
    }

    [Fact]
    public void CanonicalEdited()
    {
        var (_, full) = Paths();
        var manager = new HotReloadManager();
        manager.CommitSourceHash(full, Source);
        Assert.True(manager.HasChanges(full, "export const n = 2;"));
    }

    [Fact]
    public void DifferentModuleUnseen()
    {
        var (_, full) = Paths();
        var manager = new HotReloadManager();
        manager.CommitSourceHash(full, Source);
        string other = Path.GetFullPath(Path.Combine("hot-reload-test", "other.ts"));
        Assert.NotEqual(full, other);
        Assert.True(manager.HasChanges(other, Source));
    }

    [Fact]
    public void LegacyOpaqueKeys()
    {
        foreach (string key in new[] { "", "opaque\0key" })
        {
            var manager = new HotReloadManager();
            Assert.True(manager.HasChanges(key, Source));
            manager.CommitSourceHash(key, Source);
            Assert.False(manager.HasChanges(key, Source));
            Assert.True(manager.HasChanges(key, "edited"));
        }
    }

    [Fact]
    public void LegacyNullAndHashOrder()
    {
        var manager = new HotReloadManager();
        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() =>
        {
            manager.HasChanges(null!, Source);
        }).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() =>
            manager.CommitSourceHash(null!, Source)).ParamName);

        string? referenceHashParam = Assert.Throws<ArgumentNullException>(() =>
        {
            manager.HasChanges("ordinary.ts", null!);
        }).ParamName;
        Assert.NotNull(referenceHashParam);
        Assert.NotEqual("key", referenceHashParam);
        Assert.Equal(referenceHashParam, Assert.Throws<ArgumentNullException>(() =>
        {
            manager.HasChanges(null!, null!);
        }).ParamName);
        Assert.Equal(referenceHashParam, Assert.Throws<ArgumentNullException>(() =>
            manager.CommitSourceHash(null!, null!)).ParamName);
    }
}
