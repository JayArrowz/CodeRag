using Microsoft.EntityFrameworkCore;

namespace CodeRag.Storage.Shared;

public abstract class CodeRagDbContextBase : DbContext
{
    protected CodeRagDbContextBase(DbContextOptions options) : base(options) { }

    public DbSet<CodeChunkEntity> CodeChunks => Set<CodeChunkEntity>();
    public DbSet<CodeEdgeEntity> CodeEdges => Set<CodeEdgeEntity>();
}
