using CodeRag.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Storage.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL + pgvector vector store with Entity Framework Core.
    /// Reads <c>ConnectionString</c> and <c>EmbeddingDimensions</c> from <paramref name="config"/>.
    /// </summary>
    public static IServiceCollection AddPgVectorStore(this IServiceCollection services,
        IConfiguration config)
    {
        var connectionString = config["ConnectionString"]
            ?? "Host=localhost;Database=coderag;Username=postgres;Password=postgres";

        var embeddingDimensions = int.TryParse(config["EmbeddingDimensions"], out var d) ? d : 1536;

        // The pgvector column type needs a fixed dimension (e.g. vector(1536)) so
        // that ivfflat / hnsw indexes can be built. Capture it for the DbContext model.
        CodeRagDbContext.EmbeddingDimensions = embeddingDimensions;

        services.AddDbContextFactory<CodeRagDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
            });
        });

        services.AddSingleton<IVectorStore>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<CodeRagDbContext>>();
            return new PgVectorStore(factory, embeddingDimensions);
        });

        return services;
    }
}
