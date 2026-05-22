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
    /// Results carry a normalized cosine-similarity score (0..1, higher = better).
    /// </summary>
    Task<List<SearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Keyword / identifier search over function names, signatures, class names,
    /// namespaces, documentation, and file paths. Returns up to <paramref name="topK"/>
    /// chunks with a lexical relevance score normalized to 0..1. Use alongside the
    /// vector search and fuse with RRF for hybrid retrieval that catches symbol-name
    /// queries vectors typically miss.
    /// </summary>
    Task<List<SearchResult>> LexicalSearchAsync(string query, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Exact-identifier lookup. Returns chunks whose <c>FunctionName</c>, <c>ClassName</c>,
    /// <c>Signature</c>, or <c>Namespace</c> exactly equals (case-insensitive) the symbol —
    /// or, for dotted symbols like <c>Foo.Bar</c>, where ClassName=Foo and FunctionName=Bar.
    /// Used as a fast-path so a user query like "FileWatcherService.AddWatch" surfaces
    /// the exact definition at rank 1.
    /// </summary>
    Task<List<SearchResult>> ExactSymbolSearchAsync(string symbol, int topK = 5,
        SearchFilter? filter = null, CancellationToken ct = default);

    /// <summary>Fetch chunks by id (preserves duplicates-free behavior).</summary>
    Task<List<CodeChunk>> GetChunksByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Fetch all chunks in a file (used for sibling-method expansion).
    /// </summary>
    Task<List<CodeChunk>> GetChunksByFileAsync(string filePath, string? workspace = null, CancellationToken ct = default);

    /// <summary>
    /// Find the type-declaration chunk that contains a member: same workspace + same
    /// <paramref name="className"/> with a TypeKind. Returns null if not indexed.
    /// </summary>
    Task<CodeChunk?> GetContainingTypeAsync(string workspace, string? @namespace, string className, CancellationToken ct = default);

    /// <summary>
    /// Return all member chunks (methods, properties, fields, etc.) that belong to
    /// <paramref name="className"/> in the given workspace. Ordered by line number.
    /// </summary>
    Task<List<CodeChunk>> GetTypeMembersAsync(string workspace, string? @namespace, string className, CancellationToken ct = default);

    /// <summary>
    /// Return the type-declaration chunks for every type that directly implements or
    /// inherits from <paramref name="targetSignature"/> (resolved to an in-solution chunk
    /// via an "implements" or "inherits" edge). Pass <paramref name="workspace"/> to scope
    /// the search; pass <c>null</c> to search all workspaces.
    /// </summary>
    Task<List<CodeChunk>> GetImplementorsAsync(string targetSignature, string? workspace, CancellationToken ct = default);

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
    /// Useful for re-indexing. When <paramref name="workspace"/> is provided
    /// the delete is scoped so identical relative paths in other workspaces are untouched.
    /// </summary>
    Task DeleteByProjectAsync(string projectName, CancellationToken ct = default);
    Task DeleteByFileAsync(string filePath, string? workspace = null, CancellationToken ct = default);

    /// <summary>
    /// Return one entry per distinct file path within a workspace with the most recent
    /// indexing timestamp. Used by the file watcher to detect changes that happened
    /// while the app was offline.
    /// </summary>
    Task<List<FileIndexInfo>> ListIndexedFilesAsync(string workspace, string? projectName = null, CancellationToken ct = default);

    /// <summary>
    /// List all distinct workspaces with summary counts.
    /// </summary>
    Task<List<WorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get statistics about the stored data.
    /// </summary>
    Task<StoreStats> GetStatsAsync(CancellationToken ct = default);
}
