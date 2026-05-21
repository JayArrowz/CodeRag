using CodeRag.Storage.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using System.Text.Json;

namespace CodeRag.Storage.Sqlite;

internal class SqliteDbContext : CodeRagDbContextBase
{
    public SqliteDbContext(DbContextOptions<SqliteDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var vectorConverter = new ValueConverter<Vector?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v.ToArray(), (JsonSerializerOptions?)null),
            s => s == null ? null : new Vector(JsonSerializer.Deserialize<float[]>(s, (JsonSerializerOptions?)null)!)
        );

        var listConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            s => JsonSerializer.Deserialize<List<string>>(s, (JsonSerializerOptions?)null) ?? new List<string>()
        );

        modelBuilder.Entity<CodeChunkEntity>(entity =>
        {
            entity.Property(e => e.Embedding)
                .HasConversion(vectorConverter)
                .HasColumnType("TEXT");

            entity.Property(e => e.Modifiers).HasConversion(listConverter).HasColumnType("TEXT");
            entity.Property(e => e.Parameters).HasConversion(listConverter).HasColumnType("TEXT");
            entity.Property(e => e.Attributes).HasConversion(listConverter).HasColumnType("TEXT");
            entity.Property(e => e.Calls).HasConversion(listConverter).HasColumnType("TEXT");
            entity.Property(e => e.BaseTypes).HasConversion(listConverter).HasColumnType("TEXT");
            entity.Property(e => e.Interfaces).HasConversion(listConverter).HasColumnType("TEXT");

            entity.HasIndex(e => e.Language).HasDatabaseName("ix_chunks_language");
            entity.HasIndex(e => e.Kind).HasDatabaseName("ix_chunks_kind");
            entity.HasIndex(e => e.Workspace).HasDatabaseName("ix_chunks_workspace");
            entity.HasIndex(e => e.ProjectName).HasDatabaseName("ix_chunks_project");
            entity.HasIndex(e => e.FilePath).HasDatabaseName("ix_chunks_filepath");
            entity.HasIndex(e => e.ClassName).HasDatabaseName("ix_chunks_classname");
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
