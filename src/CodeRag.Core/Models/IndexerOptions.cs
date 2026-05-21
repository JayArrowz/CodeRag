namespace CodeRag.Core.Models;

public class IndexerOptions
{
    public int FileBatchSize { get; set; } = 20;
    public int EmbeddingBatchSize { get; set; } = 50;
    public int StoreBatchSize { get; set; } = 100;
    public List<string> ExcludePatterns { get; set; } =
    [
        "/bin/", "/obj/", "/node_modules/", "/.git/", "/dist/",
        "/__pycache__/", "/.vs/", "/packages/", "/TestResults/"
    ];
}
