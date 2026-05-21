using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;

namespace CodeRag.Core.Services;

/// <summary>
/// Orchestrates the full indexing pipeline: discover files → analyze → embed → store.
/// </summary>
public class CodebaseIndexer
{
    private readonly IReadOnlyList<ILanguageAnalyzer> _analyzers;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IndexerOptions _options;

    public CodebaseIndexer(
        IEnumerable<ILanguageAnalyzer> analyzers,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IndexerOptions? options = null)
    {
        _analyzers = analyzers.ToList();
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _options = options ?? new IndexerOptions();
    }

    public async Task<IndexingStats> IndexDirectoryAsync(string rootPath, string workspace,
        string? projectName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("Workspace is required.", nameof(workspace));

        var stats = new IndexingStats { Workspace = workspace };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        projectName ??= Path.GetFileName(rootPath);

        var analyzerMap = new Dictionary<string, ILanguageAnalyzer>();
        foreach (var analyzer in _analyzers)
            foreach (var ext in analyzer.SupportedExtensions)
                analyzerMap.TryAdd(ext, analyzer);

        var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(f => analyzerMap.ContainsKey(Path.GetExtension(f)))
            .Where(f => !IsExcluded(f))
            .ToList();

        stats.TotalFiles = files.Count;
        Console.WriteLine($"Found {files.Count} source files to analyze (workspace: {workspace}).");

        var allChunks = new List<CodeChunk>();
        var allEdges = new List<CodeEdge>();

        foreach (var batch in files.Chunk(_options.FileBatchSize))
        {
            var tasks = batch.Select(async file =>
            {
                var ext = Path.GetExtension(file);
                var analyzer = analyzerMap[ext];

                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var relativePath = Path.GetRelativePath(rootPath, file);
                    return await analyzer.AnalyzeFileAsync(relativePath, content, workspace, projectName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error analyzing {file}: {ex.Message}");
                    return AnalysisResult.Empty;
                }
            });

            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                allChunks.AddRange(r.Chunks);
                allEdges.AddRange(r.Edges);
            }
        }

        StampWorkspace(allChunks, allEdges, workspace);
        ResolveEdgeTargets(allChunks, allEdges);
        await EmbedAndStore(allChunks, allEdges, stats, ct);

        sw.Stop();
        stats.Duration = sw.Elapsed;
        return stats;
    }

    public async Task<IndexingStats> IndexSolutionAsync(string solutionPath, string workspace,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("Workspace is required.", nameof(workspace));

        var stats = new IndexingStats { Workspace = workspace };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var solutionAnalyzer = _analyzers.OfType<ISolutionAnalyzer>().FirstOrDefault()
            ?? throw new InvalidOperationException("No ISolutionAnalyzer registered. Add the RoslynAnalyzer.");

        Console.WriteLine($"Analyzing solution: {solutionPath} (workspace: {workspace})");
        var result = await solutionAnalyzer.AnalyzeSolutionAsync(solutionPath, workspace);

        StampWorkspace(result.Chunks, result.Edges, workspace);
        await EmbedAndStore(result.Chunks, result.Edges, stats, ct);

        sw.Stop();
        stats.Duration = sw.Elapsed;
        return stats;
    }

    public async Task<List<SearchResult>> QueryAsync(string query, int topK = 10,
        SearchFilter? filter = null, CancellationToken ct = default)
    {
        var embedding = await _embeddingService.EmbedAsync(query, ct);
        return await _vectorStore.SearchAsync(embedding, topK, filter, ct);
    }

    private async Task EmbedAndStore(List<CodeChunk> chunks, List<CodeEdge> edges,
        IndexingStats stats, CancellationToken ct)
    {
        Console.WriteLine($"Extracted {chunks.Count} chunks and {edges.Count} edges. Generating embeddings...");

        foreach (var batch in chunks.Chunk(_options.EmbeddingBatchSize))
        {
            var texts = batch.Select(c => c.ToEmbeddingText()).ToList();
            var embeddings = await _embeddingService.EmbedBatchAsync(texts, ct);
            for (int i = 0; i < batch.Length; i++)
                batch[i].Embedding = embeddings[i];
        }

        Console.WriteLine("Storing chunks...");
        foreach (var batch in chunks.Chunk(_options.StoreBatchSize))
            await _vectorStore.UpsertAsync(batch, ct);

        Console.WriteLine("Storing edges...");
        foreach (var batch in edges.Chunk(_options.StoreBatchSize))
            await _vectorStore.UpsertEdgesAsync(batch, ct);

        stats.TotalChunks = chunks.Count;
        stats.Methods = chunks.Count(c => c.Kind == "method_declaration");
        stats.Classes = chunks.Count(c => c.Kind is "class_declaration" or "record_declaration"
            or "struct_declaration" or "interface_declaration");
        stats.Properties = chunks.Count(c => c.Kind == "property_declaration");
        stats.LibraryCalls = edges.Count(e => e.IsExternal && e.EdgeKind == "calls");
        stats.Edges = edges.Count;
        stats.InternalEdges = edges.Count(e => !e.IsExternal);
        stats.ExternalEdges = edges.Count(e => e.IsExternal);
        stats.ByLanguage = chunks.GroupBy(c => c.Language).ToDictionary(g => g.Key, g => g.Count());
    }

    private static void ResolveEdgeTargets(List<CodeChunk> chunks, List<CodeEdge> edges)
    {
        // Build signature -> id map scoped to this indexing run (== one workspace),
        // so identical signatures across workspaces never cross-link.
        var bySignature = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.Signature)) continue;
            bySignature.TryAdd(c.Signature, c.Id);
        }

        foreach (var edge in edges)
        {
            if (edge.TargetChunkId is not null) continue;
            if (bySignature.TryGetValue(edge.TargetSignature, out var id))
            {
                edge.TargetChunkId = id;
                edge.IsExternal = false;
            }
        }
    }

    private static void StampWorkspace(List<CodeChunk> chunks, List<CodeEdge> edges, string workspace)
    {
        foreach (var c in chunks)
            if (string.IsNullOrEmpty(c.Workspace)) c.Workspace = workspace;
        foreach (var e in edges)
            if (string.IsNullOrEmpty(e.Workspace)) e.Workspace = workspace;
    }

    private bool IsExcluded(string path)
    {
        var normalized = path.Replace('\\', '/');
        return _options.ExcludePatterns.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
