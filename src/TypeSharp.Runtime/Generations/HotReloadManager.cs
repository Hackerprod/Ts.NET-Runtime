namespace TypeSharp.Runtime.Generations;

public sealed class GenerationSnapshot
{
    public int GenerationId { get; }
    public DateTime CreatedAt { get; }
    public Dictionary<string, CompiledModuleData> Modules { get; } = new();
    public bool IsActive { get; set; }

    public GenerationSnapshot(int generationId)
    {
        GenerationId = generationId;
        CreatedAt = DateTime.UtcNow;
        IsActive = true;
    }
}

public sealed class CompiledModuleData
{
    public string Name { get; }
    public string FilePath { get; }
    public string SourceHash { get; }
    public byte[] Bytecode { get; set; }

    public CompiledModuleData(string name, string filePath, string sourceHash, byte[] bytecode)
    {
        Name = name;
        FilePath = filePath;
        SourceHash = sourceHash;
        Bytecode = bytecode;
    }
}

public sealed class HotReloadManager
{
    private readonly List<GenerationSnapshot> _generations = new();
    private int _currentGenerationId;
    private readonly Dictionary<string, string> _sourceHashes = new();

    public int CurrentGenerationId => _currentGenerationId;
    public GenerationSnapshot? CurrentGeneration => _generations.LastOrDefault();

    public HotReloadManager()
    {
        _currentGenerationId = 0;
    }

    public bool HasChanges(string filePath, string newSource)
    {
        string newHash = ComputeHash(newSource);
        if (_sourceHashes.TryGetValue(filePath, out var oldHash))
        {
            return oldHash != newHash;
        }
        return true;
    }

    public GenerationSnapshot BeginNewGeneration()
    {
        _currentGenerationId++;
        var snapshot = new GenerationSnapshot(_currentGenerationId);

        foreach (var gen in _generations)
        {
            if (gen.IsActive)
            {
                gen.IsActive = false;
            }
        }

        _generations.Add(snapshot);
        return snapshot;
    }

    public void CommitSourceHash(string filePath, string source)
    {
        _sourceHashes[filePath] = ComputeHash(source);
    }

    public bool CanMigrateInstance(string typeName, string fieldName, string fieldType)
    {
        var previous = _generations
            .Where(g => g.IsActive)
            .LastOrDefault();

        if (previous == null) return false;

        if (previous.Modules.TryGetValue(typeName, out var module))
        {
            // Simple check - in production would compare full type shapes
            return true;
        }

        return false;
    }

    public void PruneGenerations(int keepLast = 3)
    {
        while (_generations.Count > keepLast)
        {
            var oldest = _generations[0];
            if (!oldest.IsActive)
            {
                _generations.RemoveAt(0);
            }
            else
            {
                break;
            }
        }
    }

    private static string ComputeHash(string source)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}
