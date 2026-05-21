using CodeRag.Storage.Postgres.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeRag.Storage.Postgres;

public class CodeRagDbContext : DbContext
{
    /// <summary>
    /// Embedding vector dimensions used when configuring the pgvector column type.
    /// Set by <see cref="ServiceCollectionExtensions.AddPgVectorStore"/> at startup.
    /// </summary>
    internal static int EmbeddingDimensions = 1536;

    public DbSet<CodeChunkEntity> CodeChunks => Set<CodeChunkEntity>();
    public DbSet<CodeEdgeEntity> CodeEdges => Set<CodeEdgeEntity>();

    public CodeRagDbContext(DbContextOptions<CodeRagDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<CodeChunkEntity>(entity =>
        {
            // pgvector requires a fixed-dimension column type (e.g. vector(1536))
            // for ivfflat / hnsw indexes to be created.
            entity.Property(e => e.Embedding)
                .HasColumnType($"vector({EmbeddingDimensions})");

            // Composite index for fast filtering
            entity.HasIndex(e => e.Language).HasDatabaseName("ix_chunks_language");
            entity.HasIndex(e => e.Kind).HasDatabaseName("ix_chunks_kind");
            entity.HasIndex(e => e.Workspace).HasDatabaseName("ix_chunks_workspace");
            entity.HasIndex(e => e.ProjectName).HasDatabaseName("ix_chunks_project");
            entity.HasIndex(e => e.FilePath).HasDatabaseName("ix_chunks_filepath");
            entity.HasIndex(e => e.ClassName).HasDatabaseName("ix_chunks_classname");

            // pgvector IVFFlat index — created via raw SQL after initial migration
            // because EF Core doesn't natively support vector index creation.
            // See InitializeAsync in PgVectorStore.
        });

        modelBuilder.Entity<CodeEdgeEntity>(entity =>
        {
            entity.HasIndex(e => e.SourceChunkId).HasDatabaseName("ix_edges_source");
            entity.HasIndex(e => e.TargetChunkId).HasDatabaseName("ix_edges_target");
            entity.HasIndex(e => e.TargetSignature).HasDatabaseName("ix_edges_target_sig");
            entity.HasIndex(e => e.EdgeKind).HasDatabaseName("ix_edges_kind");
            entity.HasIndex(e => e.FilePath).HasDatabaseName("ix_edges_filepath");
            entity.HasIndex(e => e.Workspace).HasDatabaseName("ix_edges_workspace");
            entity.HasIndex(e => e.ProjectName).HasDatabaseName("ix_edges_project");
        });
    }
}
