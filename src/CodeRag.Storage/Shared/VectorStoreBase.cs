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
        if (filter.Workspaces.Count > 0)
            query = query.Where(c => filter.Workspaces.Contains(c.Workspace));
        if (filter.Languages.Count > 0)
            query = query.Where(c => filter.Languages.Contains(c.Language));
        if (filter.Projects.Count > 0)
            query = query.Where(c => c.ProjectName != null && filter.Projects.Contains(c.ProjectName));
        if (filter.Kinds.Count > 0)
            query = query.Where(c => filter.Kinds.Contains(c.Kind));
        if (filter.FilePathContains.Count > 0)
            query = query.Where(c => filter.FilePathContains.Any(p => c.FilePath.Contains(p)));
        if (filter.ExcludeFilePathContains.Count > 0)
            query = query.Where(c => !filter.ExcludeFilePathContains.Any(p => c.FilePath.Contains(p)));
        return query;
    }

    public virtual async Task<List<SearchResult>> LexicalSearchAsync(string query, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default)
    {
        var terms = ExtractTerms(query, max: 6);
        if (terms.Count == 0) return new();

        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var q = ApplyFilter(db.CodeChunks.AsNoTracking(), filter);

        // Pull a pool of candidates matching ANY term in ANY high-signal field. Score in memory.
        // Using EF Core's translation of List<string>.Any(t => column.Contains(t)) — works on both
        // Postgres and SQLite. We lowercase the column AND the term so the LIKE is case-insensitive
        // (Postgres LIKE is case-sensitive by default).
        var lc = terms.Select(t => t.ToLowerInvariant()).ToList();
        var pool = await q.Where(c =>
                lc.Any(t => c.FunctionName.ToLower().Contains(t))
             || (c.Signature != null      && lc.Any(t => c.Signature.ToLower().Contains(t)))
             || (c.ClassName != null      && lc.Any(t => c.ClassName.ToLower().Contains(t)))
             || (c.Namespace != null      && lc.Any(t => c.Namespace.ToLower().Contains(t)))
             || (c.Documentation != null  && lc.Any(t => c.Documentation.ToLower().Contains(t)))
             ||                              lc.Any(t => c.FilePath.ToLower().Contains(t)))
            .Take(Math.Max(topK * 4, 50))
            .ToListAsync(ct);

        var scored = pool.Select(c =>
            {
                double score = 0;
                var fn = c.FunctionName?.ToLowerInvariant();
                var cn = c.ClassName?.ToLowerInvariant();
                var sg = c.Signature?.ToLowerInvariant();
                var ns = c.Namespace?.ToLowerInvariant();
                var doc = c.Documentation?.ToLowerInvariant();
                var fp = c.FilePath?.ToLowerInvariant();
                foreach (var t in lc)
                {
                    if (fn == t) score += 6;
                    else if (fn?.Contains(t) == true) score += 2;
                    if (cn == t) score += 5;
                    else if (cn?.Contains(t) == true) score += 1.5;
                    if (sg?.Contains(t) == true) score += 2;
                    if (ns?.Contains(t) == true) score += 0.5;
                    if (doc?.Contains(t) == true) score += 0.5;
                    if (fp?.Contains(t) == true) score += 0.5;
                }
                return (Chunk: c, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        var max = scored.Count > 0 ? scored.Max(x => x.Score) : 1.0;
        if (max <= 0) max = 1.0;

        return scored.Select(x => new SearchResult
        {
            Chunk = VectorStoreMapper.FromEntity(x.Chunk),
            Score = x.Score / max,  // normalize to 0..1
        }).ToList();
    }

    public virtual async Task<List<SearchResult>> ExactSymbolSearchAsync(string symbol, int topK = 5,
        SearchFilter? filter = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return new();

        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var q = ApplyFilter(db.CodeChunks.AsNoTracking(), filter);

        var s = symbol.Trim().ToLowerInvariant();
        // Dotted symbol like "Class.Member" or "Namespace.Class.Member" → split.
        var dot = s.LastIndexOf('.');
        string? memberLc = null, classLc = null;
        if (dot > 0 && dot < s.Length - 1)
        {
            memberLc = s[(dot + 1)..];
            classLc = s[..dot];
            // strip any namespace prefix in classLc for a relaxed match
            var lastDot = classLc.LastIndexOf('.');
            if (lastDot > 0) classLc = classLc[(lastDot + 1)..];
        }

        var rows = await q.Where(c =>
                c.FunctionName.ToLower() == s
             || (c.ClassName != null && c.ClassName.ToLower() == s)
             || (c.Signature != null && c.Signature.ToLower() == s)
             || (c.Namespace != null && c.Namespace.ToLower() == s)
             || (memberLc != null && classLc != null
                  && c.FunctionName.ToLower() == memberLc
                  && c.ClassName != null && c.ClassName.ToLower() == classLc))
            .Take(topK * 3)
            .ToListAsync(ct);

        // Rank: dotted member-match beats whole-symbol class hit beats whole-symbol function hit.
        var scored = rows.Select(c =>
            {
                double score = 1.0;
                var fn = c.FunctionName?.ToLowerInvariant();
                var cn = c.ClassName?.ToLowerInvariant();
                if (memberLc != null && fn == memberLc && cn == classLc) score = 4.0;
                else if (cn == s) score = 3.0;
                else if (fn == s) score = 2.5;
                else if (c.Signature?.ToLowerInvariant() == s) score = 2.0;
                return (Chunk: c, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        var max = scored.Count > 0 ? scored.Max(x => x.Score) : 1.0;
        return scored.Select(x => new SearchResult
        {
            Chunk = VectorStoreMapper.FromEntity(x.Chunk),
            Score = x.Score / max,
        }).ToList();
    }

    public async Task<List<CodeChunk>> GetChunksByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new();
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var idList = ids.ToList();
        var rows = await db.CodeChunks.AsNoTracking()
            .Where(c => idList.Contains(c.Id))
            .ToListAsync(ct);
        return rows.Select(VectorStoreMapper.FromEntity).ToList();
    }

    public async Task<List<CodeChunk>> GetChunksByFileAsync(string filePath, string? workspace = null, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var q = db.CodeChunks.AsNoTracking().Where(c => c.FilePath == filePath);
        if (!string.IsNullOrEmpty(workspace))
            q = q.Where(c => c.Workspace == workspace);
        var rows = await q.ToListAsync(ct);
        return rows.Select(VectorStoreMapper.FromEntity).ToList();
    }

    public async Task<CodeChunk?> GetContainingTypeAsync(string workspace, string? @namespace, string className, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(className)) return null;
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var typeKinds = SearchFilter.TypeKinds;
        var q = db.CodeChunks.AsNoTracking().Where(c =>
            c.Workspace == workspace
            && c.ClassName == className
            && typeKinds.Contains(c.Kind));
        if (!string.IsNullOrEmpty(@namespace))
            q = q.Where(c => c.Namespace == @namespace);
        var row = await q.FirstOrDefaultAsync(ct);
        return row is null ? null : VectorStoreMapper.FromEntity(row);
    }

    /// <summary>
    /// Split a free-form query into identifier-shaped terms. Strips short / stopword
    /// tokens and also produces camelCase / snake_case sub-tokens so a query like
    /// <c>"FileWatcherService"</c> matches <c>"file"</c>, <c>"watcher"</c>, <c>"service"</c>.
    /// </summary>
    private static List<string> ExtractTerms(string query, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var raw = System.Text.RegularExpressions.Regex
            .Matches(query, @"[A-Za-z_][A-Za-z0-9_]+")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var terms = new List<string>();
        foreach (var t in raw)
        {
            if (t.Length < 2 || IsStopword(t)) continue;
            terms.Add(t);
            // also split camelCase / PascalCase
            var sub = System.Text.RegularExpressions.Regex
                .Matches(t, @"[A-Z]?[a-z0-9]+|[A-Z]+(?=[A-Z]|$)")
                .Select(m => m.Value)
                .Where(s => s.Length >= 3 && !IsStopword(s));
            terms.AddRange(sub);
        }
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    private static readonly HashSet<string> _stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","is","are","was","were","of","to","in","on","for","and","or",
        "with","by","at","be","this","that","it","as","from","do","does","how","what",
        "where","when","why","which","who","i","you","my","your","get","set"
    };
    private static bool IsStopword(string s) => _stop.Contains(s);


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

    public async Task<List<CodeChunk>> GetTypeMembersAsync(string workspace, string? @namespace, string className, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var q = db.CodeChunks.AsNoTracking()
            .Where(c => c.Workspace == workspace && c.ClassName == className);
        if (!string.IsNullOrEmpty(@namespace))
            q = q.Where(c => c.Namespace == @namespace);
        var rows = await q.OrderBy(c => c.LineNumber).ToListAsync(ct);
        return rows.Select(VectorStoreMapper.FromEntity).ToList();
    }

    public async Task<List<CodeChunk>> GetImplementorsAsync(string targetSignature, string? workspace, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        var edgeKinds = new[] { "inherits", "implements" };
        var edgesQ = db.CodeEdges.AsNoTracking()
            .Where(e => edgeKinds.Contains(e.EdgeKind) && e.TargetSignature == targetSignature && e.TargetChunkId != null);
        if (!string.IsNullOrEmpty(workspace))
            edgesQ = edgesQ.Where(e => e.Workspace == workspace);
        var edges = await edgesQ.ToListAsync(ct);
        if (edges.Count == 0) return new();
        var sourceIds = edges.Select(e => e.SourceChunkId).Distinct().ToList();
        var typeKinds = SearchFilter.TypeKinds;
        var chunks = await db.CodeChunks.AsNoTracking()
            .Where(c => sourceIds.Contains(c.Id) && typeKinds.Contains(c.Kind))
            .ToListAsync(ct);
        return chunks.Select(VectorStoreMapper.FromEntity).ToList();
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
