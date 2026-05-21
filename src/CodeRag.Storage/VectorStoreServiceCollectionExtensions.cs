using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using CodeRag.Storage.Shared;
using CodeRag.Storage.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Storage;

public static class VectorStoreServiceCollectionExtensions
{
    public static IServiceCollection AddVectorStore(this IServiceCollection services,
        IConfiguration config)
    {
        var options = config.GetSection("Database").Get<DatabaseOptions>()
            ?? throw new InvalidOperationException("Missing 'Database' configuration section.");

        return options.Provider switch
        {
            DatabaseProviderType.Postgres => services.AddPostgresVectorStore(options),
            DatabaseProviderType.Sqlite   => services.AddSqliteVectorStore(options),
            _ => throw new NotSupportedException($"Database provider '{options.Provider}' is not supported."),
        };
    }

    private static IServiceCollection AddPostgresVectorStore(this IServiceCollection services,
        DatabaseOptions options)
    {
        var embeddingDimensions = options.EmbeddingDimensions;
        Postgres.PgDbContext.EmbeddingDimensions = embeddingDimensions;

        services.AddDbContextFactory<Postgres.PgDbContext>(o =>
            o.UseNpgsql(options.ConnectionString, npgsql => npgsql.UseVector()));

        services.AddSingleton<IDbContextFactory<CodeRagDbContextBase>>(sp =>
            new DbContextFactoryAdapter<Postgres.PgDbContext>(
                sp.GetRequiredService<IDbContextFactory<Postgres.PgDbContext>>()));

        services.AddSingleton<IVectorStore>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<Postgres.PgDbContext>>();
            return new Postgres.PgVectorStore(factory, embeddingDimensions);
        });

        return services;
    }

    private static IServiceCollection AddSqliteVectorStore(this IServiceCollection services,
        DatabaseOptions options)
    {
        services.AddDbContextFactory<SqliteDbContext>(o =>
            o.UseSqlite(options.ConnectionString));

        services.AddSingleton<IDbContextFactory<CodeRagDbContextBase>>(sp =>
            new DbContextFactoryAdapter<SqliteDbContext>(
                sp.GetRequiredService<IDbContextFactory<SqliteDbContext>>()));

        services.AddSingleton<IVectorStore>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<SqliteDbContext>>();
            return new SqliteVectorStore(factory);
        });

        return services;
    }
}

internal sealed class DbContextFactoryAdapter<TConcrete>(IDbContextFactory<TConcrete> inner)
    : IDbContextFactory<CodeRagDbContextBase>
    where TConcrete : CodeRagDbContextBase
{
    public CodeRagDbContextBase CreateDbContext() => inner.CreateDbContext();
}
