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
                Workspace = req.AllWorkspaces ? null : req.Workspace,
                Language = req.Language,
                Kind = req.Kind,
                ProjectName = req.Project,
                FilePath = req.FilePath,
            };

            var results = await indexer.QueryAsync(
                req.Query, req.TopK ?? 10, filter,
                hydrateEdges: req.HydrateEdges ?? true, ct);

            // When edges are hydrated, lift shared library docs out so callers (incl. an MCP server)
            // don't get the same XML comment N times. Per-result retrievalText emits a back-reference
            // instead of the full doc body for any signature in the shared map.
            var dedupe = (req.DedupeLibraryDocs ?? true) && (req.HydrateEdges ?? true);
            var libraryDocs = dedupe ? SearchResult.BuildLibraryDocIndex(results) : null;
            var skipSet = libraryDocs is null ? null : (ISet<string>)new HashSet<string>(libraryDocs.Keys, StringComparer.Ordinal);

            if (req.RetrievalText == true)
            {
                return Results.Ok(new
                {
                    libraryDocs,
                    results = results.Select((r, i) => new
                    {
                        rank = i + 1,
                        score = r.Score,
                        chunkId = r.Chunk.Id,
                        filePath = r.Chunk.FilePath,
                        lineNumber = r.Chunk.LineNumber,
                        retrievalText = r.ToRetrievalText(skipSet),
                    })
                });
            }

            return Results.Ok(new
            {
                libraryDocs,
                results = results.Select(r => SearchResultDto.From(r, skipSet))
            });
        })
        .WithSummary("Semantic search. Set retrievalText=true for LLM-ready text blocks.");

        // ----- stats / workspaces -----
        api.MapGet("/stats", async (IVectorStore store, CancellationToken ct) =>
            Results.Ok(await store.GetStatsAsync(ct)))
            .WithSummary("Global store statistics.");

        api.MapGet("/workspaces", async (IVectorStore store, CancellationToken ct) =>
            Results.Ok(await store.ListWorkspacesAsync(ct)))
            .WithSummary("List indexed workspaces with chunk/edge counts.");

        api.MapDelete("/workspaces/{name}", async (string name, IVectorStore store, CancellationToken ct) =>
        {
            await store.DeleteByWorkspaceAsync(name, ct);
            return Results.Ok(new { ok = true, deleted = name });
        })
        .WithSummary("Drop all chunks and edges for a workspace.");

        api.MapDelete("/projects/{name}", async (string name, IVectorStore store, CancellationToken ct) =>
        {
            await store.DeleteByProjectAsync(name, ct);
            return Results.Ok(new { ok = true, deleted = name });
        })
        .WithSummary("Drop all chunks and edges for a project.");

        api.MapDelete("/files", async (string path, IVectorStore store, CancellationToken ct) =>
        {
            await store.DeleteByFileAsync(path, ct);
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

        return app;
    }

    // ----- request / response DTOs -----
    public record IndexSolutionRequest(string Path, string Workspace);
    public record IndexDirectoryRequest(string Path, string Workspace, string? Project);

    public record QueryRequest(
        string Query,
        string? Workspace = null,
        bool AllWorkspaces = false,
        int? TopK = null,
        string? Language = null,
        string? Kind = null,
        string? Project = null,
        string? FilePath = null,
        bool? HydrateEdges = null,
        bool? RetrievalText = null,
        bool? DedupeLibraryDocs = null);

    public record SearchResultDto(double Score, CodeChunk Chunk, List<CodeEdge>? OutgoingEdges, string RetrievalText)
    {
        public static SearchResultDto From(SearchResult r, ISet<string>? skipDocSignatures = null) =>
            new(r.Score, r.Chunk, r.OutgoingEdges, r.ToRetrievalText(skipDocSignatures));
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
