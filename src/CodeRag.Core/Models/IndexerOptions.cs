namespace CodeRag.Core.Models;

public class IndexerOptions
{
    public int FileBatchSize { get; set; } = 20;
    public int EmbeddingBatchSize { get; set; } = 50;
    public int StoreBatchSize { get; set; } = 100;

    /// <summary>
    /// When true, the indexer and file watcher honor every <c>.gitignore</c> they find under
    /// the root directory (per gitignore spec, scoped to each gitignore's directory).
    /// <see cref="ExcludePatterns"/> still apply as a baseline so build outputs are skipped
    /// even when a project has no <c>.gitignore</c>.
    /// </summary>
    public bool RespectGitIgnore { get; set; } = true;

    /// <summary>
    /// Path-segment baseline excludes (applied in addition to any <c>.gitignore</c> rules).
    /// These match if the segment appears anywhere in the path.
    /// </summary>
    public List<string> ExcludePatterns { get; set; } =
    [
        "/bin/", "/obj/", "/node_modules/", "/.git/", "/dist/",
        "/__pycache__/", "/.vs/", "/packages/", "/TestResults/"
    ];
}
