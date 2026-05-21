using CodeRag.Core.Models;

namespace CodeRag.Core.Interfaces;

/// <summary>
/// Abstraction over a vector database for storing and querying code chunks.
/// Implement this to swap between Postgres+pgvector, Qdrant, ChromaDB, etc.
/// </summary>
public interface IVectorStore : IAsyncDisposable
{
    /// <summary>
    /// Ensure the database schema/collection exists. Called once at startup.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert or update a batch of chunks. Embeddings must already be populated.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken ct = default);

    /// <summary>
    /// Insert or update a batch of edges (calls / creates / inherits / implements).
    /// </summary>
    Task UpsertEdgesAsync(IReadOnlyList<CodeEdge> edges, CancellationToken ct = default);

    /// <summary>
    /// Search for the top-k most similar chunks to the given query embedding.
    /// </summary>
    Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Return edges originating from the given chunk (what it calls / creates / etc.).
    /// </summary>
    Task<List<CodeEdge>> GetOutgoingEdgesAsync(Guid sourceChunkId, CancellationToken ct = default);

    /// <summary>
    /// Return edges pointing at the given chunk (who calls it / inherits from it).
    /// </summary>
    Task<List<CodeEdge>> GetIncomingEdgesAsync(Guid targetChunkId, CancellationToken ct = default);

    /// <summary>
    /// Delete all chunks/edges associated with a given workspace.
    /// </summary>
    Task DeleteByWorkspaceAsync(string workspace, CancellationToken ct = default);

    /// <summary>
    /// Delete all chunks associated with a given project or file path.
    /// Useful for re-indexing.
    /// </summary>
    Task DeleteByProjectAsync(string projectName, CancellationToken ct = default);
    Task DeleteByFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// List all distinct workspaces with summary counts.
    /// </summary>
    Task<List<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get statistics about the stored data.
    /// </summary>
    Task<StoreStats> GetStatsAsync(CancellationToken ct = default);
}
