using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeRag.Core.Models;
using CodeRag.Core.Services;

namespace CodeRag.Dashboard.Services;

public enum JobKind { IndexSolution, IndexDirectory, DropWorkspace }
public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public class IndexingJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required JobKind Kind { get; init; }
    public required string Workspace { get; init; }
    public required string Path { get; init; }
    public string? ProjectName { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
    public IndexingStats? Stats { get; set; }
    public List<string> Log { get; } = new();
    public CancellationTokenSource Cts { get; } = new();
    public event Action? Updated;
    public void Touch() => Updated?.Invoke();
}

/// <summary>
/// Runs CodebaseIndexer work in the background, capturing console output per-job
/// (the indexer writes progress via Console.WriteLine).
/// After a job succeeds, automatically registers a FileWatcherService root for the
/// indexed path so incremental re-indexing happens on file changes.
/// </summary>
public class IndexingJobService
{
    private readonly IServiceProvider _sp;
    private readonly FileWatcherService _watcher;
    private readonly ConcurrentDictionary<Guid, IndexingJob> _jobs = new();

    public IndexingJobService(IServiceProvider sp, FileWatcherService watcher)
    {
        _sp = sp;
        _watcher = watcher;
    }

    public IReadOnlyCollection<IndexingJob> Jobs =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    public IndexingJob? Get(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IndexingJob StartIndexSolution(string solutionPath, string workspace)
    {
        var job = new IndexingJob { Kind = JobKind.IndexSolution, Workspace = workspace, Path = solutionPath };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, async (indexer, ct) =>
            await indexer.IndexSolutionAsync(solutionPath, workspace, ct)));
        return job;
    }

    public IndexingJob StartIndexDirectory(string dir, string workspace, string? projectName)
    {
        var job = new IndexingJob
        {
            Kind = JobKind.IndexDirectory,
            Workspace = workspace,
            Path = dir,
            ProjectName = projectName
        };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job, async (indexer, ct) =>
            await indexer.IndexDirectoryAsync(dir, workspace, projectName, ct)));
        return job;
    }

    public bool Cancel(Guid id)
    {
        if (_jobs.TryGetValue(id, out var job) && job.Status is JobStatus.Queued or JobStatus.Running)
        {
            job.Cts.Cancel();
            return true;
        }
        return false;
    }

    public void Remove(Guid id) => _jobs.TryRemove(id, out _);

    private async Task RunAsync(IndexingJob job, Func<CodebaseIndexer, CancellationToken, Task<IndexingStats>> work)
    {
        job.Status = JobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.Touch();

        var prevId = JobConsoleWriter.CurrentJobId.Value;
        JobConsoleWriter.CurrentJobId.Value = job.Id;
        JobConsoleWriter.Register(job);
        try
        {
            using var scope = _sp.CreateScope();
            var indexer = scope.ServiceProvider.GetRequiredService<CodebaseIndexer>();
            job.Stats = await work(indexer, job.Cts.Token);
            job.Status = JobStatus.Succeeded;
            EnsureWatch(job);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.ToString();
            job.Log.Add($"ERROR: {ex.Message}");
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            JobConsoleWriter.Unregister(job.Id);
            JobConsoleWriter.CurrentJobId.Value = prevId;
            job.Touch();
        }
    }

    private void EnsureWatch(IndexingJob job)
    {
        if (job.Kind == JobKind.IndexSolution)
            EnsureSolutionWatches(job);
        else
            EnsureDirectoryWatch(job);
    }

    /// <summary>
    /// For a directory-level index, registers one watch for the directory.
    /// Skips the initial sweep because indexing just completed.
    /// </summary>
    private void EnsureDirectoryWatch(IndexingJob job)
    {
        var watchPath = job.Path;
        if (string.IsNullOrEmpty(watchPath) || !Directory.Exists(watchPath)) return;

        var alreadyWatched = _watcher.List()
            .Any(w => string.Equals(w.Path, watchPath, StringComparison.OrdinalIgnoreCase)
                   && w.Workspace == job.Workspace);
        if (alreadyWatched) return;

        _watcher.AddWatch(new WatchedRoot
        {
            Path = watchPath,
            Workspace = job.Workspace,
            Project = job.ProjectName,
            IncludeSubdirectories = true,
        }, skipInitialSweep: true);
    }

    /// <summary>
    /// For a solution index, registers one <see cref="WatchedRoot"/> per project directory
    /// so that incremental re-indexing uses the correct per-project name rather than a
    /// single flattened watch that would lose project scoping.
    /// Skips the initial sweep because indexing just completed.
    /// </summary>
    private void EnsureSolutionWatches(IndexingJob job)
    {
        var projects = ParseSolutionProjects(job.Path);
        var existing = _watcher.List();

        foreach (var (projectPath, projectName) in projects)
        {
            var dir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            var alreadyWatched = existing.Any(w =>
                string.Equals(w.Path, dir, StringComparison.OrdinalIgnoreCase)
                && w.Workspace == job.Workspace);
            if (alreadyWatched) continue;

            _watcher.AddWatch(new WatchedRoot
            {
                Path = dir,
                Workspace = job.Workspace,
                Project = projectName,
                IncludeSubdirectories = true,
            }, skipInitialSweep: true);
        }
    }

    /// <summary>
    /// Extracts (absoluteProjectPath, projectName) pairs from a .sln or .slnx file.
    /// </summary>
    private static IReadOnlyList<(string ProjectPath, string ProjectName)> ParseSolutionProjects(
        string solutionPath)
    {
        var dir = Path.GetDirectoryName(solutionPath)!;

        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            // XML format: <Project Path="relative/path.csproj" />
            var doc = XDocument.Load(solutionPath);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "Project")
                .Select(e => e.Attribute("Path")?.Value)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetFullPath(Path.Combine(dir, p!)))
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(p => (p, Path.GetFileNameWithoutExtension(p)))
                .ToList();
        }
        else
        {
            // Classic .sln format line:
            // Project("{GUID}") = "Name", "relative\path.csproj", "{GUID}"
            var regex = new Regex(
                @"Project\(""\{[^""]+\}""\)\s*=\s*""([^""]*)""\s*,\s*""([^""]+\.csproj)""",
                RegexOptions.IgnoreCase);

            return File.ReadLines(solutionPath)
                .Select(line => regex.Match(line))
                .Where(m => m.Success)
                .Select(m => (
                    Path.GetFullPath(Path.Combine(dir,
                        m.Groups[2].Value.Replace('\\', Path.DirectorySeparatorChar))),
                    m.Groups[1].Value
                ))
                .ToList();
        }
    }
}
