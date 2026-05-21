using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using CodeRag.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace CodeRag.Storage.Sqlite;

internal class SqliteVectorStore : VectorStoreBase<SqliteDbContext>
{
    internal SqliteVectorStore(IDbContextFactory<SqliteDbContext> contextFactory)
        : base(contextFactory) { }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
    }

    public override async Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        var query = ApplyFilter(db.CodeChunks.AsNoTracking().Where(c => c.Embedding != null), filter);
        var rows = await query.ToListAsync(ct);

        return rows
            .Select(row => new SearchResult
            {
                Chunk = VectorStoreMapper.FromEntity(row),
                Score = VectorStoreHelper.CosineSimilarity(queryEmbedding, row.Embedding!.ToArray()),
            })
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }
}
