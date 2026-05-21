using System.Text.Json;

namespace CodeRag.Dashboard.Services;

/// <summary>
/// A directory the dashboard auto-syncs to the index. Persisted as JSON so the list
/// survives app restarts. On startup the <see cref="FileWatcherService"/> sweeps each
/// root for files that changed while the app was offline, then attaches a
/// <see cref="FileSystemWatcher"/> for live updates.
/// </summary>
public class WatchedRoot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Path { get; set; }
    public required string Workspace { get; set; }
    public string? Project { get; set; }
    /// <summary>
    /// Optional path to the .sln / .slnx / .csproj this watch belongs to. When set, file
    /// changes are reindexed with the full Roslyn semantic model (preserving cross-file
    /// edges like calls/inherits/library refs) instead of the structure-only fast path.
    /// </summary>
    public string? SolutionPath { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = true;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastSweepAt { get; set; }
}

/// <summary>
/// JSON-file backed store for <see cref="WatchedRoot"/>s. Lives outside the vector DB
/// so it's trivially portable and doesn't require a schema migration. Default location is
/// %LOCALAPPDATA%/CodeRag/watches.json — override via the <c>WatchesFile</c> config key.
/// </summary>
public class WatchPersistence
{
    private readonly string _file;
    private readonly object _lock = new();
    private List<WatchedRoot> _watches = new();

    public WatchPersistence(IConfiguration config)
    {
        _file = config["WatchesFile"]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeRag", "watches.json");
        Load();
    }

    public IReadOnlyList<WatchedRoot> List()
    {
        lock (_lock) return _watches.ToList();
    }

    public WatchedRoot? Get(Guid id)
    {
        lock (_lock) return _watches.FirstOrDefault(w => w.Id == id);
    }

    public WatchedRoot Add(WatchedRoot w)
    {
        lock (_lock)
        {
            _watches.Add(w);
            Save();
        }
        return w;
    }

    public bool Update(WatchedRoot w)
    {
        lock (_lock)
        {
            var idx = _watches.FindIndex(x => x.Id == w.Id);
            if (idx < 0) return false;
            _watches[idx] = w;
            Save();
            return true;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var removed = _watches.RemoveAll(w => w.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var json = File.ReadAllText(_file);
            _watches = JsonSerializer.Deserialize<List<WatchedRoot>>(json) ?? new();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WatchPersistence: could not load {_file}: {ex.Message}");
            _watches = new();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_watches, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WatchPersistence: could not save {_file}: {ex.Message}");
        }
    }
}
