using CodeRag.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Storage.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL + pgvector vector store with Entity Framework Core.
    /// </summary>
    public static IServiceCollection AddPgVectorStore(this IServiceCollection services,
        string connectionString, int embeddingDimensions = 1536)
    {
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
