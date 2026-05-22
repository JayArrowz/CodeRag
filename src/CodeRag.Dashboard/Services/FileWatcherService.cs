using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeRag.Analyzers.CSharp;
using CodeRag.Analyzers.TypeScript;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders.Physical;

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
    private readonly int _startupSweepDelaySeconds;
    private readonly bool _usePolling;
    private readonly ConcurrentDictionary<Guid, RootHandle> _roots = new();
    private readonly ConcurrentQueue<WatchEvent> _events = new();
    private const int MaxEvents = 500;
    private const int DebounceMs = 750;

    public FileWatcherService(WatchPersistence persistence, IServiceProvider sp, ILogger<FileWatcherService> log, IConfiguration config)
    {
        _persistence = persistence;
        _sp = sp;
        _log = log;
        _startupSweepDelaySeconds = config.GetValue<int>("Watcher:StartupSweepDelaySeconds", 5);
        _usePolling = config.GetValue<bool?>("Watcher:UsePolling", null) ?? IsRunningInContainer();
    }

    /// <summary>
    /// Returns <c>true</c> when the process is running inside a container (Docker, etc.) where
    /// <see cref="FileSystemWatcher"/> kernel notifications are unreliable.
    /// </summary>
    private static bool IsRunningInContainer() =>
        File.Exists("/.dockerenv") ||
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
            StringComparison.OrdinalIgnoreCase);

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
        var delaySecs = _startupSweepDelaySeconds;
        _ = Task.Run(async () =>
        {
            if (delaySecs > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySecs));
            await SweepAllAsync(CancellationToken.None);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        foreach (var h in _roots.Values) h.Dispose();
        _roots.Clear();
        return Task.CompletedTask;
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <param name="skipInitialSweep">
    /// When <c>true</c> the post-registration catch-up sweep is skipped. Pass <c>true</c>
    /// when the caller just finished indexing the root (nothing stale to sweep).
    /// </param>
    public WatchedRoot AddWatch(WatchedRoot w, bool skipInitialSweep = false)
    {
        var added = _persistence.Add(w);
        Log(new WatchEvent(DateTime.UtcNow, added.Id, added.Workspace, added.Path, WatchEventKind.RootAdded, null));
        if (added.Enabled) Attach(added);
        if (!skipInitialSweep)
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

    /// <summary>
    /// Disable all watches for <paramref name="workspace"/>, detach active FileSystemWatchers,
    /// and evict Roslyn MSBuildWorkspace caches. Watches are kept in persistence so the
    /// workspace can be re-opened later via <see cref="OpenWorkspace"/>.
    /// </summary>
    public void CloseWorkspace(string workspace)
    {
        var solutionPaths = _persistence.List()
            .Where(w => string.Equals(w.Workspace, workspace, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(w.SolutionPath))
            .Select(w => w.SolutionPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ids = _persistence.SetEnabledByWorkspace(workspace, enabled: false);

        foreach (var id in ids)
        {
            if (_roots.TryRemove(id, out var handle))
                handle.Dispose();
        }

        foreach (var path in solutionPaths)
            RoslynAnalyzer.EvictWorkspaceCache(path);
    }

    /// <summary>
    /// Permanently remove every watch for <paramref name="workspace"/>: detach
    /// FileSystemWatchers, evict the Roslyn MSBuildWorkspace cache for any C#
    /// solutions, evict the TypeScript sidecar process for any tsconfig roots,
    /// then drop the persisted watch rows. Called when the workspace itself is
    /// being deleted so its watchers don't outlive it.
    /// </summary>
    public int RemoveWorkspace(string workspace)
    {
        var rows = _persistence.List()
            .Where(w => string.Equals(w.Workspace, workspace, StringComparison.Ordinal))
            .ToList();

        var solutionPaths = rows
            .Where(w => !string.IsNullOrEmpty(w.SolutionPath))
            .Select(w => w.SolutionPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // SolutionPath pointing at tsconfig*.json is a TS sidecar session key;
        // additionally evict any tsconfig.json sitting at the root of each
        // watched path as a best-effort cleanup.
        var tsConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sp in solutionPaths)
        {
            var name = Path.GetFileName(sp);
            if (name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) &&
                sp.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                tsConfigs.Add(sp);
        }
        foreach (var w in rows)
        {
            if (string.IsNullOrEmpty(w.Path) || !Directory.Exists(w.Path)) continue;
            var top = Path.Combine(w.Path, "tsconfig.json");
            if (File.Exists(top)) tsConfigs.Add(top);
        }

        var removedIds = _persistence.RemoveByWorkspace(workspace);
        foreach (var id in removedIds)
        {
            if (_roots.TryRemove(id, out var handle))
                handle.Dispose();
        }

        foreach (var path in solutionPaths)
        {
            try { RoslynAnalyzer.EvictWorkspaceCache(path); }
            catch (Exception ex) { _log.LogWarning(ex, "Roslyn cache eviction failed for {Path}", path); }
        }
        foreach (var path in tsConfigs)
        {
            try { TsCompilerAnalyzer.EvictSession(path); }
            catch (Exception ex) { _log.LogWarning(ex, "TS sidecar eviction failed for {Path}", path); }
        }

        foreach (var w in rows)
            Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.RootRemoved, "workspace deleted"));

        return removedIds.Count;
    }

    /// <summary>
    /// Re-enable all watches for <paramref name="workspace"/>, re-attach FileSystemWatchers,
    /// and run a catch-up sweep for each root.
    /// </summary>
    public void OpenWorkspace(string workspace)
    {
        var ids = _persistence.SetEnabledByWorkspace(workspace, enabled: true);
        foreach (var id in ids)
        {
            var w = _persistence.Get(id);
            if (w is null) continue;
            Attach(w);
            _ = Task.Run(() => SweepAsync(w, CancellationToken.None));
        }
    }

    public bool IsWorkspaceClosed(string workspace) => _persistence.IsWorkspaceClosed(workspace);

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
            var ignore = indexer.CreateGitIgnoreMatcher(w.Path);
                        
            FileSystemWatcher? fsw = null;
            if (!_usePolling)
            {
                fsw = new FileSystemWatcher(w.Path)
                {
                    IncludeSubdirectories = w.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = false,
                    InternalBufferSize = 64 * 1024,
                };
            }

            var handle = new RootHandle(w, fsw, exts, ignore, _log, _usePolling);
            handle.OnReindex = path => EnqueueReindex(w, path);
            handle.OnRemove = path => EnqueueRemove(w, path);
            handle.OnError = msg => Log(new WatchEvent(DateTime.UtcNow, w.Id, w.Workspace, w.Path, WatchEventKind.Error, msg));

            if (fsw != null)
            {
                fsw.Created += handle.OnFsCreated;
                fsw.Changed += handle.OnFsChanged;
                fsw.Renamed += handle.OnFsRenamed;
                fsw.Deleted += handle.OnFsDeleted;
                fsw.Error += handle.OnFsError;
                fsw.EnableRaisingEvents = true;
            }

            _roots[w.Id] = handle;

            if (_usePolling)
                handle.StartPolling();
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
                        // Solution-aware reindex preserves semantic edges (calls, inherits,
                        // library refs) that the file-only fast path would drop. The
                        // dispatcher handles missing SolutionPath by auto-discovering a
                        // descriptor per ISolutionAnalyzer (e.g. tsconfig.json) and falls
                        // back per-file for languages without one.
                        await indexer.IndexFilesInSolutionAsync(
                            w.SolutionPath, paths, w.Workspace, w.Path, w.Project);
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
            var ignore = indexer.CreateGitIgnoreMatcher(w.Path);

            // 1) Enumerate on-disk files (relative path → absolute path + mtime).
            var search = w.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var disk = new Dictionary<string, (string Abs, DateTime Mtime)>(StringComparer.OrdinalIgnoreCase);
            foreach (var abs in Directory.EnumerateFiles(w.Path, "*.*", search))
            {
                if (!exts.Contains(Path.GetExtension(abs))) continue;
                if (indexer.IsPathExcluded(abs)) continue;
                if (ignore.IsIgnored(abs)) continue;
                var rel = Path.GetRelativePath(w.Path, abs);
                disk[rel] = (abs, File.GetLastWriteTimeUtc(abs));
            }

            // 2) Pull the indexed-files snapshot from the store.
            var indexed = await store.ListIndexedFilesAsync(w.Workspace, w.Project, ct);
            var indexedByPath = indexed.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);

            // 3) Diff. For files whose mtime advanced, confirm the content actually changed
            // via a SHA-256 hash before queuing a (potentially expensive) reindex.
            var toReindex = new List<string>();
            foreach (var (rel, info) in disk)
            {
                if (!indexedByPath.TryGetValue(rel, out var stored))
                {
                    toReindex.Add(info.Abs); // new file
                }
                else if (info.Mtime > stored.LastIndexedAt.AddSeconds(1)) // 1s slack for fs/db clock skew
                {
                    // Hash the on-disk file; skip reindex if content is identical.
                    if (stored.ContentHash is null || HashFileContent(info.Abs) != stored.ContentHash)
                        toReindex.Add(info.Abs);
                }
            }
            var toRemove = indexedByPath.Keys
                .Where(rel => !disk.ContainsKey(rel))
                .ToList();

            // 4) Apply.
            if (toReindex.Count > 0)
            {
                // Always go through IndexFilesInSolutionAsync — it groups files
                // by ISolutionAnalyzer (so TypeScript routes through the warm
                // sidecar even when no SolutionPath was recorded) and falls
                // back per-file for languages without a semantic analyzer.
                await indexer.IndexFilesInSolutionAsync(
                    w.SolutionPath, toReindex, w.Workspace, w.Path, w.Project, ct);
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

    /// <summary>SHA-256 hex digest of a file's UTF-8 text — matches the hash stored by the indexer.</summary>
    private static string HashFileContent(string absPath)
    {
        var content = File.ReadAllText(absPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private sealed class RootHandle : IDisposable
    {
        private readonly WatchedRoot _w;
        private readonly FileSystemWatcher? _fsw;
        private readonly IReadOnlySet<string> _exts;
        private readonly GitIgnoreMatcher _ignore;
        private readonly ILogger _log;
        private readonly bool _usePolling;
        private CancellationTokenSource? _pollCts;
        private const int PollIntervalMs = 3_000;

        public Action<string>? OnReindex;
        public Action<string>? OnRemove;
        public Action<string>? OnError;

        public RootHandle(WatchedRoot w, FileSystemWatcher? fsw, IReadOnlySet<string> exts, GitIgnoreMatcher ignore, ILogger log, bool usePolling = false)
        {
            _w = w;
            _fsw = fsw;
            _exts = exts;
            _ignore = ignore;
            _log = log;
            _usePolling = usePolling;
        }

        /// <summary>
        /// Starts a background polling loop that periodically diffs the directory snapshot
        /// and fires <see cref="OnReindex"/>/<see cref="OnRemove"/> for changed/deleted files.
        /// Used when <see cref="FileSystemWatcher"/> is unavailable (e.g. inside a container).
        /// </summary>
        public void StartPolling()
        {
            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            var snapshot = TakeSnapshot();
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PollIntervalMs, ct); }
                catch (OperationCanceledException) { break; }

                var current = TakeSnapshot();

                foreach (var (path, mtime) in current)
                {
                    try {
                        if (!snapshot.TryGetValue(path, out var prev) || mtime > prev)
                            OnReindex?.Invoke(path);
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(ex.Message);
                    }
                }
                
                foreach (var path in snapshot.Keys)
                {
                    
                    try {
                        if (!current.ContainsKey(path))
                            OnRemove?.Invoke(path);
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(ex.Message);
                    }
                }

                snapshot = current;
            }
        }

        private Dictionary<string, DateTime> TakeSnapshot()
        {
            var search = _w.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var abs in Directory.EnumerateFiles(_w.Path, "*.*", search))
                {
                    if (!_exts.Contains(Path.GetExtension(abs))) continue;
                    if (_ignore.IsIgnored(abs)) continue;
                    result[abs] = File.GetLastWriteTimeUtc(abs);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Poll snapshot failed for {Path}", _w.Path); }
            return result;
        }

        private bool ShouldReact(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!_exts.Contains(Path.GetExtension(path))) return false;
            // .gitignore (+ baseline build-output patterns) decides exclusion. Matcher is cached
            // per watched root so this is hot-path safe even at FSW burst rates.
            if (_ignore.IsIgnored(path)) return false;
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
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            if (_fsw is not null)
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
}
