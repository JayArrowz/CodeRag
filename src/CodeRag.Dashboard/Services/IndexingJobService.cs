using System.Collections.Concurrent;
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
/// </summary>
public class IndexingJobService
{
    private readonly IServiceProvider _sp;
    private readonly ConcurrentDictionary<Guid, IndexingJob> _jobs = new();

    public IndexingJobService(IServiceProvider sp) => _sp = sp;

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
}
