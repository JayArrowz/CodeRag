using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using CodeRag.Core.Services;
using CodeRag.Dashboard.Services;

namespace CodeRag.Dashboard.Api;

/// <summary>
/// HTTP API exposing the full CodeRag pipeline (indexing, search, stats, jobs).
/// Mirrors the CLI surface so external tooling — including a future MCP server —
/// can drive everything over HTTP without process spawning.
/// </summary>
public static class CodeRagApi
{
    public static IEndpointRouteBuilder MapCodeRagApi(this IEndpointRouteBuilder app, string prefix = "/api")
    {
        var api = app.MapGroup(prefix).WithTags("CodeRag");

        // ----- schema / lifecycle -----
        api.MapPost("/init", async (IVectorStore store, CancellationToken ct) =>
        {
            await store.InitializeAsync(ct);
            return Results.Ok(new { ok = true });
        })
        .WithSummary("Create the database schema. Idempotent.");

        // ----- indexing (async via job service) -----
        api.MapPost("/index/solution", (IndexSolutionRequest req, IndexingJobService jobs) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Workspace))
                return Results.BadRequest(new { error = "path and workspace are required" });
            if (!File.Exists(req.Path))
                return Results.BadRequest(new { error = $"file not found: {req.Path}" });

            var job = jobs.StartIndexSolution(Path.GetFullPath(req.Path), req.Workspace);
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
        })
        .WithSummary("Start indexing a .sln / .csproj. Returns a job id.");

        api.MapPost("/index/directory", (IndexDirectoryRequest req, IndexingJobService jobs) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Workspace))
                return Results.BadRequest(new { error = "path and workspace are required" });
            if (!Directory.Exists(req.Path))
                return Results.BadRequest(new { error = $"directory not found: {req.Path}" });

            var job = jobs.StartIndexDirectory(Path.GetFullPath(req.Path), req.Workspace, req.Project);
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
        })
        .WithSummary("Start indexing a source directory. Returns a job id.");

        // ----- search -----
        api.MapPost("/query", async (QueryRequest req, CodebaseIndexer indexer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new { error = "query is required" });

            var filter = new SearchFilter
            {
                Workspaces = req.AllWorkspaces
                    ? new()
                    : (req.Workspaces ?? (string.IsNullOrEmpty(req.Workspace) ? new() : new() { req.Workspace! })),
                Languages = req.Languages ?? (string.IsNullOrEmpty(req.Language) ? new() : new() { req.Language! }),
                Projects = req.Projects ?? (string.IsNullOrEmpty(req.Project) ? new() : new() { req.Project! }),
                Kinds = req.Kinds ?? (string.IsNullOrEmpty(req.Kind) ? new() : new() { req.Kind! }),
                FilePathContains = req.FilePathContains ?? (string.IsNullOrEmpty(req.FilePath) ? new() : new() { req.FilePath! }),
                ExcludeFilePathContains = req.ExcludeFilePathContains ?? new(),
            };

            var options = new QueryOptions
            {
                TopK = req.TopK ?? 10,
                Filter = filter,
                CandidateMultiplier = req.CandidateMultiplier ?? 4,
                EnableSymbolMatch = req.EnableSymbolMatch ?? true,
                EnableVector = req.EnableVector ?? true,
                EnableLexical = req.EnableLexical ?? true,
                RrfK = req.RrfK ?? 60,
                SymbolMaxHits = req.SymbolMaxHits ?? 3,
                MinVectorScore = req.MinVectorScore ?? 0.30,
                DiversifyResults = req.DiversifyResults ?? true,
                MaxPerFile = req.MaxPerFile ?? 2,
                MaxPerClass = req.MaxPerClass ?? 3,
                ExpandNeighbors = req.ExpandNeighbors ?? true,
                IncludeContainingType = req.IncludeContainingType ?? true,
                IncludeIncomingEdges = req.IncludeIncomingEdges ?? true,
                MaxIncomingEdges = req.MaxIncomingEdges ?? 8,
                HydrateOutgoingEdges = req.HydrateEdges ?? true,
                TokenBudgetPerResult = req.TokenBudgetPerResult ?? 800,
                EmbeddingQueryOverride = req.EmbeddingQueryOverride,
            };

            var results = await indexer.QueryAsync(req.Query, options, ct);

            // Dedupe shared library docs across the batch so the prompt doesn't repeat XML comments.
            var dedupe = (req.DedupeLibraryDocs ?? true) && options.HydrateOutgoingEdges;
            var libraryDocs = dedupe ? SearchResult.BuildLibraryDocIndex(results) : null;
            var skipSet = libraryDocs is null ? null : (ISet<string>)new HashSet<string>(libraryDocs.Keys, StringComparer.Ordinal);
            var budget = options.TokenBudgetPerResult;

            if (req.RetrievalText == true)
            {
                return Results.Ok(new
                {
                    libraryDocs,
                    results = results.Select((r, i) => new
                    {
                        rank = i + 1,
                        score = r.Score,
                        sourceScores = r.SourceScores,
                        chunkId = r.Chunk.Id,
                        filePath = r.Chunk.FilePath,
                        lineNumber = r.Chunk.LineNumber,
                        retrievalText = r.ToRetrievalText(skipSet, budget),
                    })
                });
            }

            return Results.Ok(new
            {
                libraryDocs,
                results = results.Select(r => SearchResultDto.From(r, skipSet, budget))
            });
        })
        .WithSummary("Hybrid AI-context search: vector + lexical + symbol fast-path, fused with RRF, plus neighborhood expansion and outgoing-edge hydration. Returns LLM-ready text blocks when retrievalText=true.");

        // ----- stats / workspaces -----
        api.MapGet("/stats", async (IVectorStore store, CancellationToken ct) =>
            Results.Ok(await store.GetStatsAsync(ct)))
            .WithSummary("Global store statistics.");

        api.MapGet("/workspaces", async (IVectorStore store, CancellationToken ct) =>
            Results.Ok(await store.ListWorkspacesAsync(ct)))
            .WithSummary("List indexed workspaces with chunk/edge counts.");

        api.MapDelete("/workspaces/{name}", async (string name, IVectorStore store, FileWatcherService watcher, CancellationToken ct) =>
        {
            // Tear down any watchers + cached analyzer sessions BEFORE dropping
            // store rows so the FileSystemWatcher can't fire a reindex against
            // a workspace that's mid-delete.
            var removedWatches = watcher.RemoveWorkspace(name);
            await store.DeleteByWorkspaceAsync(name, ct);
            return Results.Ok(new { ok = true, deleted = name, removedWatches });
        })
        .WithSummary("Drop all chunks, edges, and watchers for a workspace.");

        api.MapDelete("/projects/{name}", async (string name, IVectorStore store, CancellationToken ct) =>
        {
            await store.DeleteByProjectAsync(name, ct);
            return Results.Ok(new { ok = true, deleted = name });
        })
        .WithSummary("Drop all chunks and edges for a project.");

        api.MapGet("/files", async (string workspace, IVectorStore store, string? project, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "workspace is required" });
            return Results.Ok(await store.ListIndexedFilesAsync(workspace, project, ct));
        })
        .WithSummary("List all files indexed in a workspace with their chunk counts and last-indexed timestamp.");

        api.MapDelete("/files", async (string path, IVectorStore store, CancellationToken ct) =>
        {
            await store.DeleteByFileAsync(path, null, ct);
            return Results.Ok(new { ok = true, deleted = path });
        })
        .WithSummary("Drop all chunks and edges for a file path.");

        // ----- graph -----
        api.MapGet("/chunks/{id:guid}/edges/outgoing", async (Guid id, IVectorStore store, CancellationToken ct) =>
            Results.Ok(await store.GetOutgoingEdgesAsync(id, ct)))
            .WithSummary("Outgoing edges (calls / creates / inherits / implements) from a chunk.");

        api.MapGet("/chunks/{id:guid}/edges/incoming", async (Guid id, IVectorStore store, CancellationToken ct) =>
            Results.Ok(await store.GetIncomingEdgesAsync(id, ct)))
            .WithSummary("Incoming edges pointing at a chunk.");

        api.MapGet("/files/chunks", async (string path, string workspace, IVectorStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "path and workspace are required" });
            var chunks = await store.GetChunksByFileAsync(path, workspace, ct);
            return Results.Ok(chunks);
        })
        .WithSummary("All indexed chunks for a single file, ordered by line. Use for file-level outline.");

        api.MapGet("/types/members", async (string workspace, string className, string? @namespace, IVectorStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(className))
                return Results.BadRequest(new { error = "workspace and className are required" });
            var chunks = await store.GetTypeMembersAsync(workspace, @namespace, className, ct);
            return Results.Ok(chunks);
        })
        .WithSummary("All member chunks (methods, properties, fields) of a type. Use for full class drill-down.");

        api.MapGet("/types/implementors", async (string signature, IVectorStore store, string? workspace, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(signature))
                return Results.BadRequest(new { error = "signature is required" });
            var chunks = await store.GetImplementorsAsync(signature, workspace, ct);
            return Results.Ok(chunks);
        })
        .WithSummary("Type-declaration chunks for every type that directly implements or inherits the given signature.");

        // ----- jobs -----
        api.MapGet("/jobs", (IndexingJobService jobs) =>
            Results.Ok(jobs.Jobs.Select(j => JobDto.From(j))))
            .WithSummary("All indexing jobs (queued / running / finished).");

        api.MapGet("/jobs/{id:guid}", (Guid id, IndexingJobService jobs) =>
        {
            var job = jobs.Get(id);
            return job is null ? Results.NotFound() : Results.Ok(JobDto.From(job, includeLog: true));
        })
        .WithSummary("Single job with full log output.");

        api.MapPost("/jobs/{id:guid}/cancel", (Guid id, IndexingJobService jobs) =>
            jobs.Cancel(id) ? Results.Ok(new { ok = true }) : Results.NotFound())
            .WithSummary("Cancel a running or queued job.");

        api.MapDelete("/jobs/{id:guid}", (Guid id, IndexingJobService jobs) =>
        {
            jobs.Remove(id);
            return Results.Ok(new { ok = true });
        })
        .WithSummary("Remove a finished job from the registry.");

        // ----- file watches -----
        api.MapGet("/watches", (FileWatcherService watcher) =>
            Results.Ok(watcher.List()))
            .WithSummary("List configured directory watches (auto-reindex roots).");

        api.MapGet("/watches/events", (FileWatcherService watcher) =>
            Results.Ok(watcher.RecentEvents()))
            .WithSummary("Recent watcher events (reindex / remove / sweep / error).");

        api.MapPost("/watches", (AddWatchRequest req, FileWatcherService watcher) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Workspace))
                return Results.BadRequest(new { error = "path and workspace are required" });
            if (!Directory.Exists(req.Path))
                return Results.BadRequest(new { error = $"directory not found: {req.Path}" });

            var added = watcher.AddWatch(new WatchedRoot
            {
                Path = Path.GetFullPath(req.Path),
                Workspace = req.Workspace,
                Project = string.IsNullOrWhiteSpace(req.Project) ? null : req.Project,
                IncludeSubdirectories = req.IncludeSubdirectories ?? true,
                Enabled = req.Enabled ?? true,
            });
            return Results.Created($"/api/watches/{added.Id}", added);
        })
        .WithSummary("Register a directory to be auto-synced to the index.");

        api.MapPatch("/watches/{id:guid}", (Guid id, UpdateWatchRequest req, FileWatcherService watcher) =>
        {
            if (req.Enabled is bool enabled)
                return watcher.SetEnabled(id, enabled) ? Results.Ok(watcher.Get(id)) : Results.NotFound();
            return Results.BadRequest(new { error = "no supported fields to update" });
        })
        .WithSummary("Enable or disable a watch.");

        api.MapPost("/watches/{id:guid}/sweep", async (Guid id, FileWatcherService watcher, CancellationToken ct) =>
        {
            if (watcher.Get(id) is null) return Results.NotFound();
            await watcher.SweepNowAsync(id, ct);
            return Results.Ok(new { ok = true });
        })
        .WithSummary("Force a catch-up sweep for this watch right now.");

        api.MapDelete("/watches/{id:guid}", (Guid id, FileWatcherService watcher) =>
            watcher.RemoveWatch(id) ? Results.Ok(new { ok = true }) : Results.NotFound())
            .WithSummary("Stop watching this root (does not delete indexed data).");

        return app;
    }

    // ----- request / response DTOs -----
    public record IndexSolutionRequest(string Path, string Workspace);
    public record IndexDirectoryRequest(string Path, string Workspace, string? Project);
    public record AddWatchRequest(string Path, string Workspace, string? Project = null, bool? IncludeSubdirectories = null, bool? Enabled = null);
    public record UpdateWatchRequest(bool? Enabled);

    public record QueryRequest(
        string Query,
        // Scope (single-value sugar + multi-value)
        string? Workspace = null,
        List<string>? Workspaces = null,
        bool AllWorkspaces = false,
        string? Language = null,
        List<string>? Languages = null,
        string? Project = null,
        List<string>? Projects = null,
        string? Kind = null,
        List<string>? Kinds = null,
        string? FilePath = null,
        List<string>? FilePathContains = null,
        List<string>? ExcludeFilePathContains = null,
        // Pipeline knobs
        int? TopK = null,
        int? CandidateMultiplier = null,
        bool? EnableSymbolMatch = null,
        bool? EnableVector = null,
        bool? EnableLexical = null,
        int? RrfK = null,
        int? SymbolMaxHits = null,
        double? MinVectorScore = null,
        bool? DiversifyResults = null,
        int? MaxPerFile = null,
        int? MaxPerClass = null,
        bool? ExpandNeighbors = null,
        bool? IncludeContainingType = null,
        bool? IncludeIncomingEdges = null,
        int? MaxIncomingEdges = null,
        bool? HydrateEdges = null,
        int? TokenBudgetPerResult = null,
        string? EmbeddingQueryOverride = null,
        // Output shaping
        bool? RetrievalText = null,
        bool? DedupeLibraryDocs = null);

    public record SearchResultDto(
        double Score, CodeChunk Chunk,
        List<CodeEdge>? OutgoingEdges, List<CodeEdge>? IncomingEdges,
        List<RelatedChunk>? RelatedChunks,
        Dictionary<string, double>? SourceScores,
        string RetrievalText)
    {
        public static SearchResultDto From(SearchResult r, ISet<string>? skipDocSignatures = null, int tokenBudget = 0) =>
            new(r.Score, r.Chunk, r.OutgoingEdges, r.IncomingEdges, r.RelatedChunks, r.SourceScores,
                r.ToRetrievalText(skipDocSignatures, tokenBudget));
    }

    public record JobDto(
        Guid Id, string Kind, string Workspace, string Path, string? Project,
        string Status, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt,
        string? Error, IndexingStats? Stats, List<string>? Log)
    {
        public static JobDto From(IndexingJob j, bool includeLog = false) => new(
            j.Id, j.Kind.ToString(), j.Workspace, j.Path, j.ProjectName,
            j.Status.ToString(), j.CreatedAt, j.StartedAt, j.FinishedAt,
            j.Error, j.Stats, includeLog ? j.Log.ToList() : null);
    }
}
