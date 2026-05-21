using System.Collections.Concurrent;
using CodeRag.Core.Services;

namespace CodeRag.Dashboard.Services;

public enum WatchEventKind { ReindexFile, RemoveFile, SweepStarted, SweepCompleted, Error, RootAdded, RootRemoved, RootDisabled, RootEnabled }

public record WatchEvent(
    DateTime At,
    Guid? WatchId,
    string Workspace,
    string? FilePath,
    WatchEventKind Kind,
    string? Detail);

/// <summary>
/// Watches each configured <see cref="WatchedRoot"/> directory and incrementally updates
/// the index when source files change. Coalesces rapid filesystem bursts (saves, git checkouts,
/// build outputs) via a per-file debounce window before invoking the indexer. On host start,
/// runs a one-shot sweep per root to catch up on edits/deletions that happened while the app
/// was offline by comparing on-disk mtimes against each chunk's stored IndexedAt.
/// </summary>
public class FileWatcherService : IHostedService, IDisposable
{
    private readonly WatchPersistence _persistence;
    private readonly IServiceProvider _sp;
    private readonly ILogger<FileWatcherService> _log;
    private readonly ConcurrentDictionary<Guid, RootHandle> _roots = new();
    private readonly ConcurrentQueue<WatchEvent> _events = new();
    private const int MaxEvents = 500;
    private const int DebounceMs = 750;

    public FileWatcherService(WatchPersistence persistence, IServiceProvider sp, ILogger<FileWatcherService> log)
    {
        _persistence = persistence;
        _sp = sp;
        _log = log;
    }

    public IReadOnlyCollection<WatchEvent> RecentEvents() => _events.ToArray();
    public event Action? Changed;

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var w in _persistence.List())
        {
            if (w.Enabled)
                Attach(w);
        }
        // Kick off the catch-up sweep in the background — it can take a while for big roots.
        _ = Task.Run(() => SweepAllAsync(CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        foreach (var h in _roots.Values) h.Dispose();
        _roots.Clear();
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    public WatchedRoot AddWatch(WatchedRoot w)
    {
        var added = _persistence.Add(w);
        Log(new WatchEvent(DateTime.UtcNow, added.Id, added.Workspace, added.Path, WatchEventKind.RootAdded, null));
        if (added.Enabled) Attach(added);
        // Sweep new root immediately so any pre-existing files get indexed/refreshed.
        _ = Task.Run(() => SweepAsync(added, CancellationToken.None));
        return added;
    }

    public bool RemoveWatch(Guid id)
    {
        if (_roots.TryRemove(id, out var handle)) handle.Dispose();
        var w = _persistence.Get(id);
        var ok = _persistence.Remove(id);
        if (ok && w is not null)
            Log(new WatchEvent(DateTime.UtcNow, id, w.Workspace, w.Path, WatchEventKind.RootRemoved, null));
        return ok;
    }

    public bool SetEnabled(Guid id, bool enabled)
    {
        var w = _persistence.Get(id);
        if (w is null) return false;
        w.Enabled = enabled;
        _persistence.Update(w);
        if (enabled)
        {
            Attach(w);
            Log(new WatchEvent(DateTime.UtcNow, id, w.Workspace, w.Path, WatchEventKind.RootEnabled, null));
            _ = Task.Run(() => SweepAsync(w, CancellationToken.None));
        }
        else
        {
            if (_roots.TryRemove(id, out var handle)) handle.Dispose();
            Log(new WatchEvent(DateTime.UtcNow, id, w.Workspace, w.Path, WatchEventKind.RootDisabled, null));
        }
        return true;
    }

    public Task SweepNowAsync(Guid id, CancellationToken ct = default)
    {
        var w = _persistence.Get(id);
        return w is null ? Task.CompletedTask : SweepAsync(w, ct);
    }

    public IReadOnlyList<WatchedRoot> List() => _persistence.List();
    public WatchedRoot? Get(Guid id) => _persistence.Get(id);

    private void Attach(WatchedRoot w)
    {
        if (!Directory.Exists(w.Path))
        {
            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.Error,
                $"watch path does not exist: {w.Path}"));
            return;
        }

        if (_roots.ContainsKey(w.Id)) return;

        try
        {
            using var scope = _sp.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexer>();
            var exts = indexer.SupportedExtensions;

            var fsw = new FileSystemWatcher(w.Path)
            {
                IncludeSubdirectories = w.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                EnableRaisingEvents = false,
                InternalBufferSize = 64 * 1024,
            };

            var handle = new RootHandle(w, fsw, exts, _log);
            handle.OnReindex = path => EnqueueReindex(w, path);
            handle.OnRemove = path => EnqueueRemove(w, path);
            handle.OnError = msg => Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.Error, msg));
            fsw.Created += handle.OnFsCreated;
            fsw.Changed += handle.OnFsChanged;
            fsw.Renamed += handle.OnFsRenamed;
            fsw.Deleted += handle.OnFsDeleted;
            fsw.Error += handle.OnFsError;
            fsw.EnableRaisingEvents = true;

            _roots[w.Id] = handle;
        }
        catch (Exception ex)
        {
            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.Error,
                $"could not attach watcher: {ex.Message}"));
        }
    }
    
    private readonly ConcurrentDictionary<string, DateTime> _pendingReindex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingRemove = new(StringComparer.OrdinalIgnoreCase);
    private int _drainerRunning;

    private void EnqueueReindex(WatchedRoot w, string absPath)
    {
        _pendingReindex[Key(w.Id, absPath)] = DateTime.UtcNow;
        _pendingRemove.TryRemove(Key(w.Id, absPath), out _); // a reindex supersedes a pending delete
        EnsureDrainer();
    }

    private void EnqueueRemove(WatchedRoot w, string absPath)
    {
        _pendingRemove[Key(w.Id, absPath)] = DateTime.UtcNow;
        _pendingReindex.TryRemove(Key(w.Id, absPath), out _);
        EnsureDrainer();
    }

    private static string Key(Guid id, string path) => id + "|" + path;

    private void EnsureDrainer()
    {
        if (Interlocked.CompareExchange(ref _drainerRunning, 1, 0) != 0) return;
        _ = Task.Run(DrainLoopAsync);
    }

    private async Task DrainLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(DebounceMs);
                if (_pendingReindex.IsEmpty && _pendingRemove.IsEmpty) break;

                var cutoff = DateTime.UtcNow.AddMilliseconds(-DebounceMs);
                var reindexNow = _pendingReindex.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();
                var removeNow = _pendingRemove.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();

                // Group reindex paths by watch so we can do a single IndexFilesAsync call per root.
                var reindexByWatch = new Dictionary<Guid, List<string>>();
                foreach (var k in reindexNow)
                {
                    _pendingReindex.TryRemove(k, out _);
                    var (id, path) = ParseKey(k);
                    if (!reindexByWatch.TryGetValue(id, out var list))
                        reindexByWatch[id] = list = new();
                    list.Add(path);
                }

                foreach (var (id, paths) in reindexByWatch)
                {
                    var w = _persistence.Get(id);
                    if (w is null || !w.Enabled) continue;
                    try
                    {
                        using var scope = _sp.CreateScope();
                        var indexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexer>();
                        await indexer.IndexFilesAsync(w.Path, paths, w.Workspace, w.Project);
                        foreach (var p in paths)
                            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, p, WatchEventKind.ReindexFile, null));
                    }
                    catch (Exception ex)
                    {
                        Log(new WatchEvent(DateTime.UtcNow, id, w.Workspace, string.Join(", ", paths.Take(3)),
                            WatchEventKind.Error, $"reindex failed: {ex.Message}"));
                    }
                }

                foreach (var k in removeNow)
                {
                    _pendingRemove.TryRemove(k, out _);
                    var (id, path) = ParseKey(k);
                    var w = _persistence.Get(id);
                    if (w is null) continue;
                    try
                    {
                        using var scope = _sp.CreateScope();
                        var indexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexer>();
                        var rel = Path.GetRelativePath(w.Path, path);
                        await indexer.RemoveFileAsync(rel, w.Workspace);
                        Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, path, WatchEventKind.RemoveFile, null));
                    }
                    catch (Exception ex)
                    {
                        Log(new WatchEvent(DateTime.UtcNow, id, w.Workspace, path, WatchEventKind.Error,
                            $"remove failed: {ex.Message}"));
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _drainerRunning, 0);
            // A new event may have arrived between the empty check and us clearing the flag.
            if (!_pendingReindex.IsEmpty || !_pendingRemove.IsEmpty) EnsureDrainer();
        }
    }

    private static (Guid id, string path) ParseKey(string key)
    {
        var pipe = key.IndexOf('|');
        return (Guid.Parse(key[..pipe]), key[(pipe + 1)..]);
    }
    
    private async Task SweepAllAsync(CancellationToken ct)
    {
        foreach (var w in _persistence.List())
            if (w.Enabled) await SweepAsync(w, ct);
    }

    /// <summary>
    /// Compare on-disk files in <paramref name="w"/> with the index. Reindexes files whose
    /// mtime is newer than the most recent IndexedAt for that path, indexes new files,
    /// and removes chunks for files no longer present on disk.
    /// </summary>
    public async Task SweepAsync(WatchedRoot w, CancellationToken ct = default)
    {
        if (!Directory.Exists(w.Path))
        {
            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.Error,
                $"sweep skipped, path missing: {w.Path}"));
            return;
        }

        Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.SweepStarted, null));

        try
        {
            using var scope = _sp.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexer>();
            var store = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IVectorStore>();
            var exts = indexer.SupportedExtensions;

            // 1) Enumerate on-disk files (relative path → absolute path + mtime).
            var search = w.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var disk = new Dictionary<string, (string Abs, DateTime Mtime)>(StringComparer.OrdinalIgnoreCase);
            foreach (var abs in Directory.EnumerateFiles(w.Path, "*.*", search))
            {
                if (!exts.Contains(Path.GetExtension(abs))) continue;
                if (indexer.IsPathExcluded(abs)) continue;
                var rel = Path.GetRelativePath(w.Path, abs);
                disk[rel] = (abs, File.GetLastWriteTimeUtc(abs));
            }

            // 2) Pull the indexed-files snapshot from the store.
            var indexed = await store.ListIndexedFilesAsync(w.Workspace, w.Project, ct);
            var indexedByPath = indexed.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);

            // 3) Diff.
            var toReindex = new List<string>();
            foreach (var (rel, info) in disk)
            {
                if (!indexedByPath.TryGetValue(rel, out var stored))
                {
                    toReindex.Add(info.Abs); // new file
                }
                else if (info.Mtime > stored.LastIndexedAt.AddSeconds(1)) // 1s slack for fs/db clock skew
                {
                    toReindex.Add(info.Abs); // modified while offline
                }
            }
            var toRemove = indexedByPath.Keys
                .Where(rel => !disk.ContainsKey(rel))
                .ToList();

            // 4) Apply.
            if (toReindex.Count > 0)
            {
                await indexer.IndexFilesAsync(w.Path, toReindex, w.Workspace, w.Project, ct);
                foreach (var abs in toReindex)
                    Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, abs, WatchEventKind.ReindexFile, "sweep"));
            }
            foreach (var rel in toRemove)
            {
                await indexer.RemoveFileAsync(rel, w.Workspace, ct);
                Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, rel, WatchEventKind.RemoveFile, "sweep"));
            }

            w.LastSweepAt = DateTime.UtcNow;
            _persistence.Update(w);

            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.SweepCompleted,
                $"reindexed={toReindex.Count} removed={toRemove.Count} onDisk={disk.Count}"));
        }
        catch (Exception ex)
        {
            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.Error,
                $"sweep failed: {ex.Message}"));
        }
    }

    private void Log(WatchEvent ev)
    {
        _events.Enqueue(ev);
        while (_events.Count > MaxEvents && _events.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    private sealed class RootHandle : IDisposable
    {
        private readonly WatchedRoot _w;
        private readonly FileSystemWatcher _fsw;
        private readonly IReadOnlySet<string> _exts;
        private readonly ILogger _log;
        public Action<string>? OnReindex;
        public Action<string>? OnRemove;
        public Action<string>? OnError;

        public RootHandle(WatchedRoot w, FileSystemWatcher fsw, IReadOnlySet<string> exts, ILogger log)
        {
            _w = w;
            _fsw = fsw;
            _exts = exts;
            _log = log;
        }

        private bool ShouldReact(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!_exts.Contains(Path.GetExtension(path))) return false;
            var normalized = path.Replace('\\', '/');
            // Mirror default IndexerOptions excludes — avoids hammering on build folders.
            foreach (var p in new[] { "/bin/", "/obj/", "/node_modules/", "/.git/", "/dist/", "/__pycache__/", "/.vs/", "/packages/", "/TestResults/" })
                if (normalized.Contains(p, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public void OnFsCreated(object _, FileSystemEventArgs e)
        {
            if (ShouldReact(e.FullPath)) OnReindex?.Invoke(e.FullPath);
        }
        public void OnFsChanged(object _, FileSystemEventArgs e)
        {
            if (ShouldReact(e.FullPath)) OnReindex?.Invoke(e.FullPath);
        }
        public void OnFsRenamed(object _, RenamedEventArgs e)
        {
            if (ShouldReact(e.OldFullPath)) OnRemove?.Invoke(e.OldFullPath);
            if (ShouldReact(e.FullPath)) OnReindex?.Invoke(e.FullPath);
        }
        public void OnFsDeleted(object _, FileSystemEventArgs e)
        {
            if (ShouldReact(e.FullPath)) OnRemove?.Invoke(e.FullPath);
        }
        public void OnFsError(object _, ErrorEventArgs e) =>
            OnError?.Invoke(e.GetException().Message);

        public void Dispose()
        {
            try
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
            }
            catch { /* ignore */ }
        }
    }
}
