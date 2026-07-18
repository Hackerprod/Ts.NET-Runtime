using TypeSharp.Runtime.Generations;

namespace TypeSharp.Hosting.HotReload;

public interface IGenerationMigrator
{
    bool CanMigrate(RuntimeGeneration previous, RuntimeGeneration candidate);
    void Migrate(RuntimeGeneration previous, RuntimeGeneration candidate);
}

public sealed class HotReloadOptions
{
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(300);
    public int RetainedGenerations { get; set; } = 3;
    public bool RunStartupCanary { get; set; } = true;
    public Func<RuntimeGeneration, bool>? Canary { get; set; }
    public List<IGenerationMigrator> Migrators { get; } = new();
}
