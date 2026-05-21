using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using CodeRag.Storage.Postgres.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace CodeRag.Storage.Postgres;

/// <summary>
/// PostgreSQL + pgvector implementation of IVectorStore using Entity Framework Core.
/// </summary>
public class PgVectorStore : IVectorStore
{
    private readonly IDbContextFactory<CodeRagDbContext> _contextFactory;
    private readonly int _embeddingDimensions;

    public PgVectorStore(IDbContextFactory<CodeRagDbContext> contextFactory, int embeddingDimensions = 1536)
    {
        _contextFactory = contextFactory;
        _embeddingDimensions = embeddingDimensions;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        // No EF migrations are defined; create the schema from the current model.
        await db.Database.EnsureCreatedAsync(ct);

        // Create the IVFFlat index for vector similarity search if it doesn't exist.
        // We use cosine distance (<=>). Adjust lists count based on your dataset size.
        var indexSql = $"""
            CREATE INDEX IF NOT EXISTS ix_chunks_embedding
            ON code_chunks
            USING ivfflat (embedding vector_cosine_ops)
            WITH (lists = 100);
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(indexSql, ct);
        }
        catch (Exception ex)
        {
            // IVFFlat index requires data to exist first; HNSW doesn't.
            // Fall back to HNSW if IVFFlat fails.
            Console.Error.WriteLine($"IVFFlat index creation deferred or failed: {ex.Message}");
            Console.Error.WriteLine("Will retry with HNSW index...");

            var hnswSql = $"""
                CREATE INDEX IF NOT EXISTS ix_chunks_embedding_hnsw
                ON code_chunks
                USING hnsw (embedding vector_cosine_ops);
                """;

            try
            {
                await db.Database.ExecuteSqlRawAsync(hnswSql, ct);
            }
            catch
            {
                Console.Error.WriteLine("Vector index will be created after first data insert.");
            }
        }
    }

    public async Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entities = chunks.Select(ToEntity).ToList();
        var ids = entities.Select(e => e.Id).ToHashSet();

        // Find existing
        var existing = await db.CodeChunks
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var entity in entities)
        {
            if (existing.TryGetValue(entity.Id, out var existingEntity))
            {
                db.Entry(existingEntity).CurrentValues.SetValues(entity);
            }
            else
            {
                db.CodeChunks.Add(entity);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertEdgesAsync(IReadOnlyList<CodeEdge> edges, CancellationToken ct = default)
    {
        if (edges.Count == 0) return;
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entities = edges.Select(ToEdgeEntity).ToList();
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
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var rows = await db.CodeEdges.AsNoTracking()
            .Where(e => e.SourceChunkId == sourceChunkId)
            .ToListAsync(ct);
        return rows.Select(FromEdgeEntity).ToList();
    }

    public async Task<List<CodeEdge>> GetIncomingEdgesAsync(Guid targetChunkId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var rows = await db.CodeEdges.AsNoTracking()
            .Where(e => e.TargetChunkId == targetChunkId)
            .ToListAsync(ct);
        return rows.Select(FromEdgeEntity).ToList();
    }

    public async Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var queryVector = new Vector(queryEmbedding);

        IQueryable<CodeChunkEntity> query = db.CodeChunks;

        // Apply filters
        if (filter is not null)
        {
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
        }

        // Order by cosine distance (ascending = most similar first)
        var results = await query
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
            .Take(topK)
            .Select(c => new
            {
                Chunk = c,
                Distance = c.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        return results.Select(r => new SearchResult
        {
            Chunk = FromEntity(r.Chunk),
            Score = 1.0 - r.Distance // Convert distance to similarity score
        }).ToList();
    }

    public async Task DeleteByWorkspaceAsync(string workspace, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        await db.CodeEdges.Where(e => e.Workspace == workspace).ExecuteDeleteAsync(ct);
        await db.CodeChunks.Where(c => c.Workspace == workspace).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteByProjectAsync(string projectName, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        await db.CodeEdges.Where(e => e.ProjectName == projectName).ExecuteDeleteAsync(ct);
        await db.CodeChunks.Where(c => c.ProjectName == projectName).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteByFileAsync(string filePath, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        await db.CodeEdges.Where(e => e.FilePath == filePath).ExecuteDeleteAsync(ct);
        await db.CodeChunks.Where(c => c.FilePath == filePath).ExecuteDeleteAsync(ct);
    }

    public async Task<List<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

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
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

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

    private static CodeChunkEntity ToEntity(CodeChunk chunk) => new()
    {
        Id = chunk.Id,
        Kind = chunk.Kind,
        Language = chunk.Language,
        Namespace = chunk.Namespace,
        ClassName = chunk.ClassName,
        FunctionName = chunk.FunctionName,
        Signature = chunk.Signature,
        FilePath = chunk.FilePath,
        LineNumber = chunk.LineNumber,
        EndLineNumber = chunk.EndLineNumber,
        Documentation = chunk.Documentation,
        Body = chunk.Body,
        BodySummary = chunk.BodySummary,
        LibraryAssembly = chunk.LibraryAssembly,
        LibraryPackage = chunk.LibraryPackage,
        Workspace = chunk.Workspace,
        ProjectName = chunk.ProjectName,
        ReturnType = chunk.ReturnType,
        Modifiers = chunk.Modifiers,
        Parameters = chunk.Parameters,
        Attributes = chunk.Attributes,
        Calls = chunk.Calls,
        BaseTypes = chunk.BaseTypes,
        Interfaces = chunk.Interfaces,
        Embedding = chunk.Embedding is not null ? new Vector(chunk.Embedding) : null,
        IndexedAt = chunk.IndexedAt,
    };

    private static CodeChunk FromEntity(CodeChunkEntity entity) => new()
    {
        Id = entity.Id,
        Kind = entity.Kind,
        Language = entity.Language,
        Namespace = entity.Namespace,
        ClassName = entity.ClassName,
        FunctionName = entity.FunctionName,
        Signature = entity.Signature,
        FilePath = entity.FilePath,
        LineNumber = entity.LineNumber,
        EndLineNumber = entity.EndLineNumber,
        Documentation = entity.Documentation,
        Body = entity.Body,
        BodySummary = entity.BodySummary,
        LibraryAssembly = entity.LibraryAssembly,
        LibraryPackage = entity.LibraryPackage,
        Workspace = entity.Workspace,
        ProjectName = entity.ProjectName,
        ReturnType = entity.ReturnType,
        Modifiers = entity.Modifiers,
        Parameters = entity.Parameters,
        Attributes = entity.Attributes,
        Calls = entity.Calls,
        BaseTypes = entity.BaseTypes,
        Interfaces = entity.Interfaces,
        Embedding = entity.Embedding?.ToArray(),
        IndexedAt = entity.IndexedAt,
    };

    private static CodeEdgeEntity ToEdgeEntity(CodeEdge edge) => new()
    {
        Id = edge.Id,
        SourceChunkId = edge.SourceChunkId,
        SourceSignature = Truncate(edge.SourceSignature, 2000),
        TargetChunkId = edge.TargetChunkId,
        TargetSignature = Truncate(edge.TargetSignature, 2000),
        TargetNamespace = TruncateOpt(edge.TargetNamespace, 500),
        TargetClassName = TruncateOpt(edge.TargetClassName, 300),
        TargetMemberName = TruncateOpt(edge.TargetMemberName, 300),
        TargetAssembly = TruncateOpt(edge.TargetAssembly, 300),
        EdgeKind = edge.EdgeKind,
        IsExternal = edge.IsExternal,
        FilePath = edge.FilePath,
        LineNumber = edge.LineNumber,
        Workspace = edge.Workspace,
        ProjectName = edge.ProjectName,
        Language = edge.Language,
    };

    private static CodeEdge FromEdgeEntity(CodeEdgeEntity e) => new()
    {
        Id = e.Id,
        SourceChunkId = e.SourceChunkId,
        SourceSignature = e.SourceSignature,
        TargetChunkId = e.TargetChunkId,
        TargetSignature = e.TargetSignature,
        TargetNamespace = e.TargetNamespace,
        TargetClassName = e.TargetClassName,
        TargetMemberName = e.TargetMemberName,
        TargetAssembly = e.TargetAssembly,
        EdgeKind = e.EdgeKind,
        IsExternal = e.IsExternal,
        FilePath = e.FilePath,
        LineNumber = e.LineNumber,
        Workspace = e.Workspace,
        ProjectName = e.ProjectName,
        Language = e.Language,
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    private static string? TruncateOpt(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s.Substring(0, max));
}
