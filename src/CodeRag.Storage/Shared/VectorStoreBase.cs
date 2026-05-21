using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeRag.Storage.Shared;

internal abstract class VectorStoreBase<TContext> : IVectorStore
    where TContext : CodeRagDbContextBase
{
    protected readonly IDbContextFactory<TContext> ContextFactory;

    protected VectorStoreBase(IDbContextFactory<TContext> contextFactory)
    {
        ContextFactory = contextFactory;
    }

    public abstract Task InitializeAsync(CancellationToken ct = default);

    public abstract Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default);

    protected static IQueryable<CodeChunkEntity> ApplyFilter(
        IQueryable<CodeChunkEntity> query, SearchFilter? filter)
    {
        if (filter is null) return query;
        if (!string.IsNullOrEmpty(filter.Workspace))
            query = query.Where(c => c.Workspace == filter.Workspace);
        if (!string.IsNullOrEmpty(filter.Language))
            query = query.Where(c => c.Language == filter.Language);
        if (!string.IsNullOrEmpty(filter.ProjectName))
            query = query.Where(c => c.ProjectName == filter.ProjectName);
        if (!string.IsNullOrEmpty(filter.Kind))
            query = query.Where(c => c.Kind == filter.Kind);
        if (!string.IsNullOrEmpty(filter.FilePath))
            query = query.Where(c => c.FilePath.Contains(filter.FilePath));
        return query;
    }

    public async Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        var entities = chunks.Select(VectorStoreMapper.ToEntity).ToList();
        var ids = entities.Select(e => e.Id).ToHashSet();

        var existing = await db.CodeChunks
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var entity in entities)
        {
            if (existing.TryGetValue(entity.Id, out var existingEntity))
                db.Entry(existingEntity).CurrentValues.SetValues(entity);
            else
                db.CodeChunks.Add(entity);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertEdgesAsync(IReadOnlyList<CodeEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        var entities = edges.Select(VectorStoreMapper.ToEdgeEntity).ToList();
        var ids = entities.Select(e => e.Id).ToHashSet();

        var existing = await db.CodeEdges
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        foreach (var entity in entities)
        {
            if (existing.TryGetValue(entity.Id, out var existingEntity))
                db.Entry(existingEntity).CurrentValues.SetValues(entity);
            else
                db.CodeEdges.Add(entity);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<CodeEdge>> GetOutgoingEdgesAsync(Guid sourceChunkId, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var rows = await db.CodeEdges.AsNoTracking()
            .Where(e => e.SourceChunkId == sourceChunkId)
            .ToListAsync(ct);
        return rows.Select(VectorStoreMapper.FromEdgeEntity).ToList();
    }

    public async Task<List<CodeEdge>> GetIncomingEdgesAsync(Guid targetChunkId, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var rows = await db.CodeEdges.AsNoTracking()
            .Where(e => e.TargetChunkId == targetChunkId)
            .ToListAsync(ct);
        return rows.Select(VectorStoreMapper.FromEdgeEntity).ToList();
    }

    public async Task DeleteByWorkspaceAsync(string workspace, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        await db.CodeEdges.Where(e => e.Workspace == workspace).ExecuteDeleteAsync(ct);
        await db.CodeChunks.Where(c => c.Workspace == workspace).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteByProjectAsync(string projectName, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        await db.CodeEdges.Where(e => e.ProjectName == projectName).ExecuteDeleteAsync(ct);
        await db.CodeChunks.Where(c => c.ProjectName == projectName).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteByFileAsync(string filePath, string? workspace = null, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        if (workspace is null)
        {
            await db.CodeEdges.Where(e => e.FilePath == filePath).ExecuteDeleteAsync(ct);
            await db.CodeChunks.Where(c => c.FilePath == filePath).ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.CodeEdges.Where(e => e.FilePath == filePath && e.Workspace == workspace).ExecuteDeleteAsync(ct);
            await db.CodeChunks.Where(c => c.FilePath == filePath && c.Workspace == workspace).ExecuteDeleteAsync(ct);
        }
    }

    public async Task<List<FileIndexInfo>> ListIndexedFilesAsync(string workspace, string? projectName = null, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var query = db.CodeChunks.Where(c => c.Workspace == workspace);
        if (!string.IsNullOrEmpty(projectName))
            query = query.Where(c => c.ProjectName == projectName);

        return await query
            .GroupBy(c => new { c.FilePath, c.Workspace, c.ProjectName })
            .Select(g => new FileIndexInfo
            {
                FilePath = g.Key.FilePath,
                Workspace = g.Key.Workspace,
                ProjectName = g.Key.ProjectName,
                LastIndexedAt = g.Max(c => c.IndexedAt),
                ChunkCount = g.Count(),
            })
            .ToListAsync(ct);
    }

    public async Task<List<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        var chunkAgg = await db.CodeChunks
            .GroupBy(c => c.Workspace)
            .Select(g => new
            {
                Workspace = g.Key,
                Chunks = g.LongCount(),
                LastIndexedAt = (DateTime?)g.Max(c => c.IndexedAt),
            })
            .ToListAsync(ct);

        var edgeCounts = await db.CodeEdges
            .GroupBy(e => e.Workspace)
            .Select(g => new { Workspace = g.Key, Edges = g.LongCount() })
            .ToDictionaryAsync(x => x.Workspace, x => x.Edges, ct);

        var langCounts = await db.CodeChunks
            .GroupBy(c => new { c.Workspace, c.Language })
            .Select(g => new { g.Key.Workspace, g.Key.Language, Count = g.LongCount() })
            .ToListAsync(ct);

        var projCounts = await db.CodeChunks
            .Where(c => c.ProjectName != null)
            .GroupBy(c => new { c.Workspace, c.ProjectName })
            .Select(g => new { g.Key.Workspace, ProjectName = g.Key.ProjectName!, Count = g.LongCount() })
            .ToListAsync(ct);

        return chunkAgg.Select(w => new WorkspaceInfo
        {
            Workspace = w.Workspace,
            Chunks = w.Chunks,
            Edges = edgeCounts.TryGetValue(w.Workspace, out var e) ? e : 0,
            LastIndexedAt = w.LastIndexedAt,
            ByLanguage = langCounts
                .Where(l => l.Workspace == w.Workspace)
                .ToDictionary(l => l.Language, l => l.Count),
            ByProject = projCounts
                .Where(p => p.Workspace == w.Workspace)
                .ToDictionary(p => p.ProjectName, p => p.Count),
        })
        .OrderBy(w => w.Workspace)
        .ToList();
    }

    public async Task<StoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        var total = await db.CodeChunks.LongCountAsync(ct);

        var byLanguage = await db.CodeChunks
            .GroupBy(c => c.Language)
            .Select(g => new { Language = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Language, x => x.Count, ct);

        var byKind = await db.CodeChunks
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Kind, x => x.Count, ct);

        var byProject = await db.CodeChunks
            .Where(c => c.ProjectName != null)
            .GroupBy(c => c.ProjectName!)
            .Select(g => new { Project = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Project, x => x.Count, ct);

        var byWorkspace = await db.CodeChunks
            .GroupBy(c => c.Workspace)
            .Select(g => new { Workspace = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Workspace, x => x.Count, ct);

        return new StoreStats
        {
            TotalChunks = total,
            ByLanguage = byLanguage,
            ByKind = byKind,
            ByProject = byProject,
            ByWorkspace = byWorkspace,
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
