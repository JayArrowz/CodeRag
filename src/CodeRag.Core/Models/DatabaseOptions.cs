namespace CodeRag.Core.Models;

public enum DatabaseProviderType
{
    Postgres,
    Sqlite,
}

/// <summary>
/// Configuration block bound from the <c>Database</c> section of appsettings.
/// </summary>
/// <example>
/// Postgres:
/// <code>
/// "Database": {
///   "Provider": "Postgres",
///   "ConnectionString": "Host=localhost;Database=coderag;Username=postgres;Password=..."
/// }
/// </code>
/// SQLite:
/// <code>
/// "Database": {
///   "Provider": "Sqlite",
///   "ConnectionString": "Data Source=coderag.db"
/// }
/// </code>
/// </example>
public class DatabaseOptions
{
    public DatabaseProviderType Provider { get; set; } = DatabaseProviderType.Postgres;
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Required for Postgres: fixes the vector column dimension so ivfflat/hnsw
    /// indexes can be built. Must match the embedding model's output dimension.
    /// Ignored for SQLite.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 1536;
}
