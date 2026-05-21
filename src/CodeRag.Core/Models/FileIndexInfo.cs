namespace CodeRag.Core.Models;

/// <summary>
/// Summary of one indexed file within a workspace. Returned by
/// <see cref="Interfaces.IVectorStore.ListIndexedFilesAsync"/> so the file watcher
/// can compare on-disk timestamps against the last indexing time.
/// </summary>
public class FileIndexInfo
{
    public required string FilePath { get; init; }
    public required string Workspace { get; init; }
    public string? ProjectName { get; init; }
    public DateTime LastIndexedAt { get; init; }
    public int ChunkCount { get; init; }
}
