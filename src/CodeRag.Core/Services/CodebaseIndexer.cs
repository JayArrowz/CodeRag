using System.Security.Cryptography;
using System.Text;
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

        var ignore = CreateGitIgnoreMatcher(rootPath);
        var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(f => analyzerMap.ContainsKey(Path.GetExtension(f)))
            .Where(f => !IsExcluded(f))
            .Where(f => !ignore.IsIgnored(f))
            .ToList();

        var allChunks = new List<CodeChunk>();
        var allEdges = new List<CodeEdge>();

        // --- Project-level analyzer dispatch (currently: TypeScript) -------------
        // Some languages need a whole-project pass to resolve symbols. If an
        // ISolutionAnalyzer is registered for an extension and we can find its
        // project descriptor in the tree (e.g. tsconfig.json for .ts/.tsx),
        // hand all matching files off to it and exclude them from the per-file
        // loop below.
        var consumedByProject = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solutionAnalyzer in _analyzers.OfType<ISolutionAnalyzer>())
        {
            var projectPath = FindProjectDescriptor(rootPath, solutionAnalyzer);
            if (projectPath is null) continue;

            Console.WriteLine($"Running project-level {solutionAnalyzer.LanguageName} analysis: {projectPath}");
            try
            {
                var projResult = await solutionAnalyzer.AnalyzeSolutionAsync(projectPath, workspace);
                allChunks.AddRange(projResult.Chunks);
                allEdges.AddRange(projResult.Edges);
                foreach (var ext in solutionAnalyzer.SupportedExtensions)
                    foreach (var f in files.Where(x => string.Equals(Path.GetExtension(x), ext, StringComparison.OrdinalIgnoreCase)))
                        consumedByProject.Add(f);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Project-level {solutionAnalyzer.LanguageName} analysis failed ({ex.Message}); " +
                    "falling back to per-file analysis for those files.");
            }
        }
        if (consumedByProject.Count > 0)
            files = files.Where(f => !consumedByProject.Contains(f)).ToList();

        stats.TotalFiles = files.Count + consumedByProject.Count;
        Console.WriteLine($"Found {stats.TotalFiles} source files to analyze (workspace: {workspace}).");

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
                    var result = await analyzer.AnalyzeFileAsync(relativePath, content, workspace, projectName);
                    var hash = ComputeFileHash(content);
                    foreach (var c in result.Chunks) c.FileHash = hash;
                    return result;
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

        StampWorkspace(allChunks, allEdges, workspace, projectName);
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

        var solutionAnalyzer = PickSolutionAnalyzer(solutionPath)
            ?? throw new InvalidOperationException(
                $"No ISolutionAnalyzer is registered that can handle '{solutionPath}'. " +
                "Add the matching analyzer (e.g. RoslynAnalyzer for .sln/.csproj, " +
                "TsCompilerAnalyzer for tsconfig.json).");

        Console.WriteLine($"Analyzing solution: {solutionPath} (workspace: {workspace})");
        var result = await solutionAnalyzer.AnalyzeSolutionAsync(solutionPath, workspace);

        // Derive a project name from the solution/config path so chunks are tagged consistently.
        var derivedProject = Path.GetFileName(solutionPath).Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase)
            ? (Path.GetFileName(Path.GetDirectoryName(solutionPath)) ?? Path.GetFileNameWithoutExtension(solutionPath))
            : Path.GetFileNameWithoutExtension(solutionPath);
        StampWorkspace(result.Chunks, result.Edges, workspace, derivedProject);
        await EmbedAndStore(result.Chunks, result.Edges, stats, ct);

        sw.Stop();
        stats.Duration = sw.Elapsed;
        return stats;
    }

    /// <summary>
    /// Pick the <see cref="ISolutionAnalyzer"/> responsible for a given solution / project
    /// descriptor path. Dispatch is by descriptor filename so the indexer stays
    /// language-agnostic.
    /// </summary>
    private ISolutionAnalyzer? PickSolutionAnalyzer(string solutionOrProjectPath)
    {
        var name = Path.GetFileName(solutionOrProjectPath);
        var ext  = Path.GetExtension(solutionOrProjectPath).ToLowerInvariant();

        // tsconfig*.json → TypeScript
        var isTsConfig = name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase)
                         && ext == ".json";
        // .sln / .csproj / .vbproj / .fsproj → first analyzer that handles .cs (Roslyn today)
        var isDotnet = ext is ".sln" or ".csproj" or ".vbproj" or ".fsproj";

        foreach (var sa in _analyzers.OfType<ISolutionAnalyzer>())
        {
            var exts = sa.SupportedExtensions;
            if (isTsConfig && (exts.Contains(".ts") || exts.Contains(".tsx"))) return sa;
            if (isDotnet && exts.Contains(".cs")) return sa;
        }
        // Fallback: first registered.
        return _analyzers.OfType<ISolutionAnalyzer>().FirstOrDefault();
    }

    /// <summary>
    /// Full hybrid retrieval pipeline tuned for AI context generation:
    /// <list type="number">
    ///   <item>Symbol-exact fast path (identifier-shaped queries pin direct hits at the top).</item>
    ///   <item>Vector ANN search over embeddings (overfetched candidate pool).</item>
    ///   <item>Lexical search over names / signatures / docs / paths.</item>
    ///   <item>Reciprocal Rank Fusion of the two candidate lists.</item>
    ///   <item>Score-threshold pruning of weak vector matches.</item>
    ///   <item>Diversity cap (per file / per class) so one neighborhood can't dominate.</item>
    ///   <item>Neighborhood expansion (containing type + incoming edges) per result.</item>
    ///   <item>Outgoing-edge hydration so <see cref="SearchResult.ToRetrievalText"/> can
    ///         include external-library docs.</item>
    /// </list>
    /// Every stage is individually toggleable via <see cref="QueryOptions"/>.
    /// </summary>
    public async Task<List<SearchResult>> QueryAsync(string query, QueryOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new QueryOptions();
        var topK = Math.Max(1, options.TopK);
        var pool = Math.Max(topK + 10, topK * Math.Max(1, options.CandidateMultiplier));
        var filter = options.Filter;

        // --- 1 + 2 + 3: symbol / (embed → vector) / lexical run concurrently --------
        // Symbol and lexical are plain DB queries; vector needs an embedding first but
        // both of the other paths can overlap with the OpenAI round-trip.

        var symbolTask = options.EnableSymbolMatch && LooksLikeSymbol(query)
            ? _vectorStore.ExactSymbolSearchAsync(query.Trim(), options.SymbolMaxHits, filter, ct)
            : Task.FromResult(new List<SearchResult>());

        var lexicalTask = options.EnableLexical
            ? _vectorStore.LexicalSearchAsync(query, pool, filter, ct)
            : Task.FromResult(new List<SearchResult>());

        var vectorTask = options.EnableVector
            ? EmbedThenSearchAsync(query, options, pool, filter, ct)
            : Task.FromResult(new List<SearchResult>());

        await Task.WhenAll(symbolTask, lexicalTask, vectorTask);

        var symbolHits = symbolTask.Result;
        var lexicalHits = lexicalTask.Result;
        var vectorHits  = vectorTask.Result;

        foreach (var s in symbolHits) s.SourceScores = new() { ["symbol"] = s.Score };
        foreach (var v in vectorHits)  v.SourceScores ??= new();
        foreach (var l in lexicalHits) { l.SourceScores ??= new(); l.SourceScores["lexical"] = l.Score; }

        // --- 4. RRF fuse vector + lexical ---
        var fused = ReciprocalRankFusion(
            new[] { vectorHits, lexicalHits },
            options.RrfK);

        // --- 5. score threshold (only prunes pure vector matches; symbol hits exempt) ---
        if (options.MinVectorScore > 0)
        {
            var symbolIds = symbolHits.Select(s => s.Chunk.Id).ToHashSet();
            fused = fused
                .Where(r => symbolIds.Contains(r.Chunk.Id)
                    || (r.SourceScores?.GetValueOrDefault("vector") ?? 0) >= options.MinVectorScore
                    || (r.SourceScores?.GetValueOrDefault("lexical") ?? 0) > 0)
                .ToList();
        }

        // --- 6. merge symbol hits at the top, dedupe by chunk id ---
        var seen = new HashSet<Guid>();
        var merged = new List<SearchResult>();
        foreach (var r in symbolHits.Concat(fused))
        {
            if (seen.Add(r.Chunk.Id))
                merged.Add(r);
        }

        // --- 7. diversity cap ---
        if (options.DiversifyResults)
            merged = ApplyDiversityCap(merged, options.MaxPerFile, options.MaxPerClass);

        var top = merged.Take(topK).ToList();

        // --- 8 + 9: neighborhood expansion and outgoing hydration run concurrently ----
        var expandTask = options.ExpandNeighbors
            ? Task.WhenAll(top.Select(r => ExpandOneAsync(r, options, ct)))
            : Task.CompletedTask;

        var hydrateTask = options.HydrateOutgoingEdges
            ? Task.WhenAll(top.Select(async r =>
                r.OutgoingEdges = await _vectorStore.GetOutgoingEdgesAsync(r.Chunk.Id, ct)))
            : Task.CompletedTask;

        await Task.WhenAll(expandTask, hydrateTask);

        return top;
    }

    /// <summary>
    /// Reciprocal Rank Fusion: combine multiple ranked candidate lists into one.
    /// score = Σ_i 1 / (k + rank_i). Robust to score scale differences across sources.
    /// </summary>
    private static List<SearchResult> ReciprocalRankFusion(
        IEnumerable<IReadOnlyList<SearchResult>> rankedLists, int k)
    {
        var byId = new Dictionary<Guid, SearchResult>();
        var fused = new Dictionary<Guid, double>();

        foreach (var list in rankedLists)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                var id = r.Chunk.Id;
                var contribution = 1.0 / (k + i + 1);
                fused[id] = fused.TryGetValue(id, out var prev) ? prev + contribution : contribution;

                if (byId.TryGetValue(id, out var existing))
                {
                    if (r.SourceScores is not null)
                    {
                        existing.SourceScores ??= new();
                        foreach (var kv in r.SourceScores)
                            existing.SourceScores[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    byId[id] = r;
                }
            }
        }

        return fused
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                var r = byId[kv.Key];
                r.Score = kv.Value;
                return r;
            })
            .ToList();
    }

    /// <summary>
    /// Greedy diversity filter: enforces a per-file and per-class cap so the prompt
    /// doesn't end up dominated by one neighborhood.
    /// </summary>
    private static List<SearchResult> ApplyDiversityCap(
        List<SearchResult> ranked, int maxPerFile, int maxPerClass)
    {
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SearchResult>();

        foreach (var r in ranked)
        {
            var fileKey = r.Chunk.FilePath ?? "";
            var classKey = $"{r.Chunk.Namespace}|{r.Chunk.ClassName}";

            var fc = perFile.GetValueOrDefault(fileKey);
            var cc = perClass.GetValueOrDefault(classKey);
            if (maxPerFile > 0 && fc >= maxPerFile) continue;
            if (maxPerClass > 0 && !string.IsNullOrEmpty(r.Chunk.ClassName) && cc >= maxPerClass) continue;

            perFile[fileKey] = fc + 1;
            perClass[classKey] = cc + 1;
            kept.Add(r);
        }
        return kept;
    }

    /// <summary>
    /// Attach the containing type chunk and incoming-edge list to a single result.
    /// Called via <c>Task.WhenAll</c> so all results are expanded concurrently.
    /// </summary>
    private async Task ExpandOneAsync(SearchResult r, QueryOptions opts, CancellationToken ct)
    {
        var c = r.Chunk;

        var containingTypeTask = (opts.IncludeContainingType
            && !string.IsNullOrEmpty(c.ClassName)
            && !SearchFilter.TypeKinds.Contains(c.Kind))
            ? _vectorStore.GetContainingTypeAsync(c.Workspace, c.Namespace, c.ClassName!, ct)
            : Task.FromResult<CodeChunk?>(null);

        var incomingTask = opts.IncludeIncomingEdges
            ? _vectorStore.GetIncomingEdgesAsync(c.Id, ct)
            : Task.FromResult(new List<CodeEdge>());

        await Task.WhenAll(containingTypeTask, incomingTask);

        var typeChunk = containingTypeTask.Result;
        if (typeChunk is not null && typeChunk.Id != c.Id)
        {
            r.RelatedChunks ??= new();
            r.RelatedChunks.Add(new RelatedChunk { Chunk = typeChunk, Relation = "containing-type" });
        }

        var inEdges = incomingTask.Result;
        if (inEdges.Count > 0)
            r.IncomingEdges = inEdges.Take(opts.MaxIncomingEdges).ToList();
    }

    /// <summary>
    /// Embed the query then run vector ANN search. Kept as a separate method so it
    /// can be awaited concurrently with the symbol and lexical tasks.
    /// </summary>
    private async Task<List<SearchResult>> EmbedThenSearchAsync(
        string query, QueryOptions options, int pool, SearchFilter? filter, CancellationToken ct)
    {
        var embedQuery = string.IsNullOrWhiteSpace(options.EmbeddingQueryOverride)
            ? query : options.EmbeddingQueryOverride!;
        var embedding = await _embeddingService.EmbedAsync(embedQuery, ct);
        var hits = await _vectorStore.SearchAsync(embedding, pool, filter, ct);
        foreach (var v in hits)
            v.SourceScores = new() { ["vector"] = v.Score };
        return hits;
    }

    /// <summary>
    /// Heuristic: query looks like a code identifier when it's a single dotted token
    /// of letters / digits / underscores. Triggers the exact-symbol fast path.
    /// </summary>
    private static bool LooksLikeSymbol(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var q = query.Trim();
        if (q.Length > 200) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(q, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$");
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
        // Fallback: bare function name -> (language -> id). Lets us resolve TS/JS call
        // sites whose AST text is just "foo" against a chunk whose full Signature is
        // "src.i18n.I18nProvider.foo(...)". Scoped per-language so a TS call never
        // resolves to a same-named C# method.
        var byNameByLang = new Dictionary<(string lang, string name), Guid>();
        foreach (var c in chunks)
        {
            if (!string.IsNullOrEmpty(c.Signature))
                bySignature.TryAdd(c.Signature, c.Id);
            if (!string.IsNullOrEmpty(c.FunctionName) && !string.IsNullOrEmpty(c.Language))
                byNameByLang.TryAdd((c.Language, c.FunctionName), c.Id);
        }

        foreach (var edge in edges)
        {
            if (edge.TargetChunkId is not null) continue;

            if (bySignature.TryGetValue(edge.TargetSignature, out var id))
            {
                edge.TargetChunkId = id;
                edge.IsExternal = false;
                continue;
            }

            // Bare-name fallback for languages whose call sites don't carry a fully-
            // qualified signature (TypeScript/JavaScript). Only fires when the target
            // looks like a single identifier or simple member access ("obj.method").
            if (string.IsNullOrEmpty(edge.Language)) continue;
            var bare = ExtractBareName(edge.TargetSignature);
            if (bare is null) continue;
            if (byNameByLang.TryGetValue((edge.Language, bare), out var nid))
            {
                edge.TargetChunkId = nid;
                edge.IsExternal = false;
            }
        }
    }

    /// <summary>
    /// Extracts the last identifier from a call-target text like <c>foo</c>,
    /// <c>obj.foo</c>, or <c>ns.cls.foo</c>. Returns null if the text contains
    /// characters that suggest it isn't a simple member-access chain
    /// (parentheses, brackets, generics, etc.).
    /// </summary>
    private static string? ExtractBareName(string targetSig)
    {
        if (string.IsNullOrEmpty(targetSig)) return null;
        foreach (var ch in targetSig)
            if (ch is '(' or ')' or '[' or ']' or '<' or '>' or ' ' or '\n' or '\r' or '\t')
                return null;
        var lastDot = targetSig.LastIndexOf('.');
        var name = lastDot < 0 ? targetSig : targetSig[(lastDot + 1)..];
        return name.Length == 0 ? null : name;
    }

    private static void StampWorkspace(List<CodeChunk> chunks, List<CodeEdge> edges, string workspace, string? projectName = null)
    {
        foreach (var c in chunks)
        {
            if (string.IsNullOrEmpty(c.Workspace)) c.Workspace = workspace;
            if (!string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(c.ProjectName)) c.ProjectName = projectName;
        }
        foreach (var e in edges)
        {
            if (string.IsNullOrEmpty(e.Workspace)) e.Workspace = workspace;
            if (!string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(e.ProjectName)) e.ProjectName = projectName;
        }
    }

    private bool IsExcluded(string path)
    {
        var normalized = path.Replace('\\', '/');
        return _options.ExcludePatterns.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Locate a project descriptor (e.g. <c>tsconfig.json</c>) inside <paramref name="rootPath"/>
    /// that the given solution-level analyzer can consume. Returns the first match (top-level
    /// preferred), or <c>null</c> if no descriptor exists or this analyzer doesn't need one
    /// for per-directory indexing.
    ///
    /// Only languages whose per-file analysis is degraded without project context are
    /// auto-routed through the ISolutionAnalyzer here. C# stays per-file in directory mode
    /// because Roslyn's <c>AnalyzeFileAsync</c> produces usable output on its own; callers
    /// who want full semantic resolution invoke <see cref="IndexSolutionAsync"/> explicitly.
    /// </summary>
    private static string? FindProjectDescriptor(string rootPath, ISolutionAnalyzer analyzer)
    {
        var patterns = new List<string>();
        foreach (var ext in analyzer.SupportedExtensions)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".ts":
                case ".tsx":
                    patterns.Add("tsconfig.json");
                    break;
                // .cs / .csproj / .sln are intentionally NOT auto-routed: see XML doc.
            }
        }

        foreach (var pat in patterns.Distinct())
        {
            var top = Directory.EnumerateFiles(rootPath, pat, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (top is not null) return top;
            var deep = Directory.EnumerateFiles(rootPath, pat, SearchOption.AllDirectories).FirstOrDefault();
            if (deep is not null) return deep;
        }
        return null;
    }

    /// <summary>
    /// Reindex a set of files in-place: deletes any existing chunks/edges keyed by each
    /// file's path (scoped to <paramref name="workspace"/>), reanalyzes the file, then
    /// upserts fresh chunks/edges. Used by the file watcher to incrementally update the
    /// index when source files change on disk. <paramref name="absoluteFiles"/> may contain
    /// paths whose extension isn't supported — those are silently skipped.
    /// </summary>
    public async Task<IndexingStats> IndexFilesAsync(string rootPath, IEnumerable<string> absoluteFiles,
        string workspace, string? projectName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("Workspace is required.", nameof(workspace));

        var stats = new IndexingStats { Workspace = workspace };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        projectName ??= Path.GetFileName(rootPath);

        var analyzerMap = BuildAnalyzerMap();
        var ignore = CreateGitIgnoreMatcher(rootPath);
        var files = absoluteFiles
            .Where(File.Exists)
            .Where(f => analyzerMap.ContainsKey(Path.GetExtension(f)))
            .Where(f => !IsExcluded(f))
            .Where(f => !ignore.IsIgnored(f))
            .Distinct()
            .ToList();

        stats.TotalFiles = files.Count;
        if (files.Count == 0)
        {
            sw.Stop();
            stats.Duration = sw.Elapsed;
            return stats;
        }

        var allChunks = new List<CodeChunk>();
        var allEdges = new List<CodeEdge>();

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            var analyzer = analyzerMap[ext];
            string content;
            try { content = await File.ReadAllTextAsync(file, ct); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading {file}: {ex.Message}");
                continue;
            }

            var relativePath = Path.GetRelativePath(rootPath, file);
            // Wipe old chunks/edges for this file before reinserting.
            await _vectorStore.DeleteByFileAsync(relativePath, workspace, ct);

            try
            {
                var result = await analyzer.AnalyzeFileAsync(relativePath, content, workspace, projectName);
                var hash = ComputeFileHash(content);
                foreach (var c in result.Chunks) c.FileHash = hash;
                allChunks.AddRange(result.Chunks);
                allEdges.AddRange(result.Edges);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error analyzing {file}: {ex.Message}");
            }
        }

        StampWorkspace(allChunks, allEdges, workspace, projectName);
        ResolveEdgeTargets(allChunks, allEdges);
        await EmbedAndStore(allChunks, allEdges, stats, ct);

        sw.Stop();
        stats.Duration = sw.Elapsed;
        return stats;
    }

    /// <summary>Remove all chunks/edges for the given relative file path in a workspace.</summary>
    public Task RemoveFileAsync(string relativeFilePath, string workspace, CancellationToken ct = default) =>
        _vectorStore.DeleteByFileAsync(relativeFilePath, workspace, ct);

    /// <summary>
    /// Semantic-aware incremental reindex: re-analyze the given files **inside the context of
    /// their parent solution** so cross-file edges (calls / inherits / library refs) are preserved.
    /// Uses a cached MSBuildWorkspace so repeated invocations are cheap. Falls back to the
    /// structure-only path when no <see cref="ISolutionAnalyzer"/> for C# is registered.
    /// </summary>
    /// <param name="solutionPath">Absolute path to the .sln/.slnx/.csproj that owns these files.</param>
    /// <param name="absoluteFiles">Absolute paths to the files that changed on disk.</param>
    /// <param name="workspace">Logical workspace name to tag chunks/edges with.</param>
    /// <param name="projectDir">
    /// Project directory used to compute the relative <c>FilePath</c> stored in the DB.
    /// Must match what the file watcher uses or the next sweep will see a path mismatch.
    /// </param>
    public async Task<IndexingStats> IndexFilesInSolutionAsync(
        string? solutionPath,
        IEnumerable<string> absoluteFiles,
        string workspace,
        string projectDir,
        string? projectName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("Workspace is required.", nameof(workspace));

        projectName ??= Path.GetFileName(projectDir);

        var stats = new IndexingStats { Workspace = workspace };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var allFiles = absoluteFiles
            .Where(File.Exists)
            .Where(f => !IsExcluded(f))
            .Distinct()
            .ToList();

        stats.TotalFiles = allFiles.Count;
        if (allFiles.Count == 0)
        {
            sw.Stop();
            stats.Duration = sw.Elapsed;
            return stats;
        }

        // Group files by which ISolutionAnalyzer claims the extension. Files that no
        // ISolutionAnalyzer handles fall through to the per-file path so JS / Python /
        // tree-sitter-only languages still get reindexed by the watcher.
        var solutionAnalyzers = _analyzers.OfType<ISolutionAnalyzer>().ToList();
        var groups = new Dictionary<ISolutionAnalyzer, List<string>>();
        var leftover = new List<string>();
        foreach (var f in allFiles)
        {
            var ext = Path.GetExtension(f);
            var sa = solutionAnalyzers.FirstOrDefault(a =>
                a.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
            if (sa is null)
            {
                leftover.Add(f);
                continue;
            }
            if (!groups.TryGetValue(sa, out var bucket))
                groups[sa] = bucket = new List<string>();
            bucket.Add(f);
        }

        var combinedChunks = new List<CodeChunk>();
        var combinedEdges  = new List<CodeEdge>();

        foreach (var (analyzer, files) in groups)
        {
            // Pick the project descriptor for this analyzer. Prefer the caller's
            // solutionPath when it's the right kind of file (.sln for Roslyn,
            // tsconfig.json for TS, etc.); otherwise discover one under projectDir.
            string? descriptor = null;
            if (!string.IsNullOrEmpty(solutionPath) && File.Exists(solutionPath))
            {
                var solExt = Path.GetExtension(solutionPath);
                var matchesAnalyzer =
                    (analyzer.LanguageName == "csharp" &&
                        (solExt.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                         solExt.Equals(".csproj", StringComparison.OrdinalIgnoreCase))) ||
                    (analyzer.LanguageName == "typescript" &&
                        Path.GetFileName(solutionPath).StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) &&
                        solExt.Equals(".json", StringComparison.OrdinalIgnoreCase));
                if (matchesAnalyzer) descriptor = solutionPath;
            }
            descriptor ??= FindProjectDescriptor(projectDir, analyzer);

            if (descriptor is null)
            {
                // No project descriptor available — degrade these files to per-file.
                leftover.AddRange(files);
                continue;
            }

            // Wipe old chunks/edges before re-emit.
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(projectDir, file);
                await _vectorStore.DeleteByFileAsync(relativePath, workspace, ct);
            }

            try
            {
                var result = await analyzer.AnalyzeFilesInSolutionAsync(descriptor, files, workspace);
                StampFileHashes(result.Chunks, files, projectDir);
                combinedChunks.AddRange(result.Chunks);
                combinedEdges.AddRange(result.Edges);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Semantic reindex via {analyzer.LanguageName} failed ({ex.Message}); " +
                    $"falling back to structure-only for {files.Count} file(s).");
                leftover.AddRange(files);
            }
        }

        // Per-file fallback for unhandled / failed files.
        if (leftover.Count > 0)
        {
            foreach (var file in leftover)
            {
                var relativePath = Path.GetRelativePath(projectDir, file);
                await _vectorStore.DeleteByFileAsync(relativePath, workspace, ct);
            }
            foreach (var file in leftover)
            {
                var analyzer = _analyzers.FirstOrDefault(a =>
                    a.SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
                if (analyzer is null) continue;
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var res = await analyzer.AnalyzeFileAsync(file, content, workspace, projectName);
                    var hash = ComputeFileHash(content);
                    foreach (var c in res.Chunks) c.FileHash = hash;
                    combinedChunks.AddRange(res.Chunks);
                    combinedEdges.AddRange(res.Edges);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Per-file reindex of '{file}' failed: {ex.Message}");
                }
            }
        }

        StampWorkspace(combinedChunks, combinedEdges, workspace, projectName);
        await EmbedAndStore(combinedChunks, combinedEdges, stats, ct);

        sw.Stop();
        stats.Duration = sw.Elapsed;
        return stats;
    }

    /// <summary>Set of file extensions (incl. leading dot) all registered analyzers can process.</summary>
    public IReadOnlySet<string> SupportedExtensions =>
        _analyzers.SelectMany(a => a.SupportedExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a path matches one of the configured exclude patterns (bin, obj, .git, ...).</summary>
    public bool IsPathExcluded(string path) => IsExcluded(path);

    /// <summary>
    /// Build a <see cref="GitIgnoreMatcher"/> seeded with the configured baseline patterns,
    /// or a no-op matcher (only baseline patterns) when <see cref="IndexerOptions.RespectGitIgnore"/>
    /// is false. Cheap to construct; callers (incl. the file watcher) should cache per root.
    /// </summary>
    public GitIgnoreMatcher CreateGitIgnoreMatcher(string rootPath)
    {
        return new GitIgnoreMatcher(rootPath, _options.ExcludePatterns, loadGitIgnores: _options.RespectGitIgnore);
    }

    /// <summary>SHA-256 hex digest of a file's UTF-8 text content.</summary>
    internal static string ComputeFileHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// Reads each absolute file path and stamps the SHA-256 hash on every chunk
    /// whose <see cref="CodeChunk.FilePath"/> matches the corresponding relative path.
    /// Used after solution-level analysis where the analyzer reads files internally.
    /// </summary>
    private static void StampFileHashes(List<CodeChunk> chunks, List<string> absoluteFiles, string projectDir)
    {
        var hashByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var abs in absoluteFiles)
        {
            try
            {
                var content = File.ReadAllText(abs);
                hashByRel[Path.GetRelativePath(projectDir, abs)] = ComputeFileHash(content);
            }
            catch { /* best effort */ }
        }
        foreach (var chunk in chunks)
        {
            if (hashByRel.TryGetValue(chunk.FilePath, out var hash))
                chunk.FileHash = hash;
        }
    }

    private Dictionary<string, ILanguageAnalyzer> BuildAnalyzerMap()
    {
        var map = new Dictionary<string, ILanguageAnalyzer>(StringComparer.OrdinalIgnoreCase);
        foreach (var analyzer in _analyzers)
            foreach (var ext in analyzer.SupportedExtensions)
                map.TryAdd(ext, analyzer);
        return map;
    }
}
