using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using CodeRag.Storage.Shared;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace CodeRag.Storage.Postgres;

internal class PgVectorStore : VectorStoreBase<PgDbContext>
{
    private readonly int _embeddingDimensions;

    internal PgVectorStore(IDbContextFactory<PgDbContext> contextFactory, int embeddingDimensions = 1536)
        : base(contextFactory)
    {
        _embeddingDimensions = embeddingDimensions;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        await db.Database.EnsureCreatedAsync(ct);

        var fixDimSql = $"""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'code_chunks' AND column_name = 'embedding'
                ) THEN
                    BEGIN
                        ALTER TABLE code_chunks
                            ALTER COLUMN embedding TYPE vector({_embeddingDimensions})
                            USING embedding::vector({_embeddingDimensions});
                    EXCEPTION WHEN others THEN
                        NULL;
                    END;
                END IF;
            END$$;
            """;
        try
        {
            await db.Database.ExecuteSqlRawAsync(fixDimSql, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not enforce embedding column dimension: {ex.Message}");
        }

        // pgvector ivfflat and hnsw both cap at 2000 dimensions for the `vector` type.
        // Higher-dimensional models (e.g. Gemini at 3072) must rely on sequential scans.
        if (_embeddingDimensions > 2000)
        {
            Console.Error.WriteLine(
                $"Skipping vector index: pgvector ivfflat/hnsw require ≤ 2000 dimensions " +
                $"(configured: {_embeddingDimensions}). Searches will use sequential scans.");
        }
        else
        {
            var indexSql = """
                CREATE INDEX IF NOT EXISTS ix_chunks_embedding
                ON code_chunks
                USING hnsw (embedding vector_cosine_ops);
                """;
            try
            {
                await db.Database.ExecuteSqlRawAsync(indexSql, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Vector index creation failed: {ex.Message}");
            }
        }
    }

    public override async Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default)
    {
        await using var db = await ContextFactory.CreateDbContextAsync(ct);

        var queryVector = new Vector(queryEmbedding);
        var query = ApplyFilter(db.CodeChunks.Where(c => c.Embedding != null), filter);

        var results = await query
            .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
            .Take(topK)
            .Select(c => new { Chunk = c, Distance = c.Embedding!.CosineDistance(queryVector) })
            .ToListAsync(ct);

        return results.Select(r => new SearchResult
        {
            Chunk = VectorStoreMapper.FromEntity(r.Chunk),
            Score = 1.0 - r.Distance,
        }).ToList();
    }
}
