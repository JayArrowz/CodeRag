using CodeRag.Analyzers.CSharp;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using CodeRag.Core.Services;
using CodeRag.Storage.Embeddings;
using CodeRag.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("CODERAG_")
    .Build();

var openAiKey = config["OpenAiApiKey"] ?? "";
var embeddingModel = config["EmbeddingModel"] ?? "text-embedding-3-small";
var embeddingDimensions = int.TryParse(config["EmbeddingDimensions"], out var d) ? d : 1536;
var defaultWorkspace = config["DefaultWorkspace"]; // optional; used when --workspace omitted on query

var services = new ServiceCollection();

services.AddPgVectorStore(config);

if (!string.IsNullOrEmpty(openAiKey))
{
    services.AddSingleton<IEmbeddingService>(
        new OpenAiEmbeddingService(openAiKey, embeddingModel, embeddingDimensions));
}
else
{
    Console.WriteLine("⚠  No OpenAI API key found. Using fake embeddings (search won't be meaningful).");
    Console.WriteLine("   Set CODERAG_OPENAIAPI_KEY or add OpenAiApiKey to appsettings.json.");
    services.AddSingleton<IEmbeddingService>(new FakeEmbeddingService(embeddingDimensions));
}

services.AddSingleton<ILanguageAnalyzer, RoslynAnalyzer>();
services.AddSingleton<CodebaseIndexer>();

var sp = services.BuildServiceProvider();

if (args.Length == 0) { PrintUsage(); return; }

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "init":               await Init();                       break;
    case "index-solution":     await IndexSolution(args);          break;
    case "index-dir":          await IndexDirectory(args);         break;
    case "query":              await Query(args);                  break;
    case "stats":              await Stats();                      break;
    case "list-workspaces":    await ListWorkspaces();             break;
    case "drop-workspace":     await DropWorkspace(args);          break;
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        break;
}

async Task Init()
{
    Console.WriteLine("Initializing database...");
    var store = sp.GetRequiredService<IVectorStore>();
    await store.InitializeAsync();
    Console.WriteLine("Done. Database and tables created.");
}

async Task IndexSolution(string[] args)
{
    var (positional, opts) = ParseArgs(args, 1);
    if (positional.Count < 1)
    {
        Console.Error.WriteLine("Usage: index-solution <path/to/Solution.sln> --workspace <name>");
        return;
    }
    var path = positional[0];
    var workspace = opts.GetValueOrDefault("workspace");
    if (string.IsNullOrWhiteSpace(workspace))
    {
        Console.Error.WriteLine("Error: --workspace <name> is required for indexing.");
        return;
    }

    if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: {path}"); return; }

    var store = sp.GetRequiredService<IVectorStore>();
    await store.InitializeAsync();

    var indexer = sp.GetRequiredService<CodebaseIndexer>();
    var stats = await indexer.IndexSolutionAsync(Path.GetFullPath(path), workspace);

    PrintStats(stats);
}

async Task IndexDirectory(string[] args)
{
    var (positional, opts) = ParseArgs(args, 1);
    if (positional.Count < 1)
    {
        Console.Error.WriteLine("Usage: index-dir <path> --workspace <name> [--project <name>]");
        return;
    }
    var path = positional[0];
    var workspace = opts.GetValueOrDefault("workspace");
    var project = opts.GetValueOrDefault("project");
    if (string.IsNullOrWhiteSpace(workspace))
    {
        Console.Error.WriteLine("Error: --workspace <name> is required for indexing.");
        return;
    }

    if (!Directory.Exists(path)) { Console.Error.WriteLine($"Directory not found: {path}"); return; }

    var store = sp.GetRequiredService<IVectorStore>();
    await store.InitializeAsync();

    var indexer = sp.GetRequiredService<CodebaseIndexer>();
    var stats = await indexer.IndexDirectoryAsync(Path.GetFullPath(path), workspace, project);

    PrintStats(stats);
}

async Task Query(string[] args)
{
    var (positional, opts) = ParseArgs(args, 1);
    if (positional.Count == 0)
    {
        Console.Error.WriteLine("Usage: query <search terms> [--workspace <name>] [--all-workspaces] [--top N] [--lang <l>] [--kind <k>] [--project <p>] [--expand]");
        return;
    }

    var queryText = string.Join(" ", positional);
    int topK = int.TryParse(opts.GetValueOrDefault("top"), out var t) ? t : 10;
    var lang = opts.GetValueOrDefault("lang");
    var kind = opts.GetValueOrDefault("kind");
    var project = opts.GetValueOrDefault("project");
    var workspace = opts.GetValueOrDefault("workspace") ?? defaultWorkspace;
    var allWorkspaces = opts.ContainsKey("all-workspaces");

    if (!allWorkspaces && string.IsNullOrWhiteSpace(workspace))
    {
        Console.Error.WriteLine("Error: no workspace specified. Pass --workspace <name>, set DefaultWorkspace in appsettings.json,");
        Console.Error.WriteLine("       set CODERAG_DEFAULTWORKSPACE env var, or use --all-workspaces to search everything.");
        return;
    }

    var scope = allWorkspaces ? "ALL workspaces" : $"workspace '{workspace}'";
    Console.WriteLine($"Searching for: \"{queryText}\" in {scope} (top {topK})");

    var filter = new SearchFilter
    {
        Workspace = allWorkspaces ? null : workspace,
        Language = lang,
        Kind = kind,
        ProjectName = project,
    };
    var indexer = sp.GetRequiredService<CodebaseIndexer>();
    var store = sp.GetRequiredService<IVectorStore>();
    bool lean = opts.ContainsKey("lean");
    var results = await indexer.QueryAsync(queryText, topK, filter, hydrateEdges: !lean);

    if (results.Count == 0)
    {
        Console.WriteLine("No results found.");
        return;
    }

    bool expand = opts.ContainsKey("expand");
    bool retrievalText = opts.ContainsKey("retrieval-text");

    if (retrievalText)
    {
        var libDocs = SearchResult.BuildLibraryDocIndex(results);
        var skip = libDocs.Count == 0 ? null : (ISet<string>)new HashSet<string>(libDocs.Keys, StringComparer.Ordinal);

        if (libDocs.Count > 0)
        {
            Console.WriteLine("// === referenced APIs (shared across results) ===");
            foreach (var (sig, doc) in libDocs)
            {
                Console.WriteLine($"// {sig}");
                foreach (var line in doc.Split('\n'))
                    Console.WriteLine($"//   {line.TrimEnd()}");
            }
            Console.WriteLine();
        }

        // Emit the exact text an LLM would receive — useful for piping into prompts.
        for (int i = 0; i < results.Count; i++)
        {
            Console.WriteLine($"// === result {i + 1}/{results.Count}  score {results[i].Score:F4} ===");
            Console.WriteLine(results[i].ToRetrievalText(skip));
            Console.WriteLine();
        }
        return;
    }

    Console.WriteLine($"\n{"Score",-8} {"Kind",-22} {"Name",-40} {"Location",-50}");
    Console.WriteLine(new string('-', 120));

    foreach (var r in results)
    {
        var c = r.Chunk;
        var name = string.IsNullOrEmpty(c.ClassName) ? c.FunctionName : $"{c.ClassName}.{c.FunctionName}";
        var loc = $"{c.FilePath}:{c.LineNumber}";

        Console.WriteLine($"{r.Score:F4}  {c.Kind,-22} {name,-40} {loc,-50}");
        if (!string.IsNullOrEmpty(c.Workspace) || !string.IsNullOrEmpty(c.ProjectName))
            Console.WriteLine($"         [workspace: {c.Workspace}{(c.ProjectName is null ? "" : $" / {c.ProjectName}")}]");
        if (!string.IsNullOrEmpty(c.Signature))
            Console.WriteLine($"         {c.Signature}");
        if (c.Modifiers.Count > 0)
            Console.WriteLine($"         modifiers: {string.Join(" ", c.Modifiers)}");
        if (c.Attributes.Count > 0)
            Console.WriteLine($"         attributes: {string.Join(" ", c.Attributes.Select(a => $"[{a}]"))}");
        if (c.BaseTypes.Count > 0)
            Console.WriteLine($"         inherits: {string.Join(", ", c.BaseTypes)}");
        if (c.Interfaces.Count > 0)
            Console.WriteLine($"         implements: {string.Join(", ", c.Interfaces)}");
        if (c.Calls.Count > 0)
            Console.WriteLine($"         calls ({c.Calls.Count}): {string.Join(", ", c.Calls.Take(5))}{(c.Calls.Count > 5 ? ", ..." : "")}");

        // Outgoing edges are hydrated by default — show external library docs inline.
        if (r.OutgoingEdges is { Count: > 0 })
        {
            var externals = r.OutgoingEdges
                .Where(e => e.IsExternal && !string.IsNullOrWhiteSpace(e.TargetDocumentation))
                .GroupBy(e => e.TargetSignature).Select(g => g.First()).Take(5).ToList();
            foreach (var e in externals)
            {
                var firstDocLine = e.TargetDocumentation!
                    .Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                Console.WriteLine($"         lib {e.TargetSignature}");
                if (firstDocLine.Length > 0)
                    Console.WriteLine($"             {Truncate(firstDocLine, 140)}");
            }
        }

        if (expand)
        {
            var outgoing = r.OutgoingEdges ?? await store.GetOutgoingEdgesAsync(c.Id);
            var incoming = await store.GetIncomingEdgesAsync(c.Id);

            if (incoming.Count > 0)
            {
                Console.WriteLine($"         -- called by ({incoming.Count}) --");
                foreach (var e in incoming.Take(10))
                    Console.WriteLine($"            {e.EdgeKind}: {e.SourceSignature}  @ {e.FilePath}:{e.LineNumber}");
            }
            if (outgoing.Count > 0)
            {
                Console.WriteLine($"         -- calls ({outgoing.Count}) --");
                foreach (var e in outgoing.Take(10))
                {
                    var ext = e.IsExternal ? $" [ext: {e.TargetAssembly}]" : "";
                    Console.WriteLine($"            {e.EdgeKind}: {e.TargetSignature}{ext}");
                }
            }
        }

        Console.WriteLine();
    }
}

static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

async Task Stats()
{
    var store = sp.GetRequiredService<IVectorStore>();
    var stats = await store.GetStatsAsync();

    Console.WriteLine($"\nTotal chunks: {stats.TotalChunks}");
    Console.WriteLine("\nBy workspace:");
    foreach (var (ws, count) in stats.ByWorkspace)
        Console.WriteLine($"  {ws,-30} {count}");
    Console.WriteLine("\nBy language:");
    foreach (var (lang, count) in stats.ByLanguage)
        Console.WriteLine($"  {lang,-15} {count}");
    Console.WriteLine("\nBy kind:");
    foreach (var (kind, count) in stats.ByKind)
        Console.WriteLine($"  {kind,-25} {count}");
    Console.WriteLine("\nBy project:");
    foreach (var (proj, count) in stats.ByProject)
        Console.WriteLine($"  {proj,-30} {count}");
}

async Task ListWorkspaces()
{
    var store = sp.GetRequiredService<IVectorStore>();
    var workspaces = await store.ListWorkspacesAsync();

    if (workspaces.Count == 0)
    {
        Console.WriteLine("No workspaces indexed yet.");
        return;
    }

    Console.WriteLine($"\n{"Workspace",-30} {"Chunks",10} {"Edges",10} {"Last indexed",-25}");
    Console.WriteLine(new string('-', 80));
    foreach (var w in workspaces)
    {
        var when = w.LastIndexedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "-";
        Console.WriteLine($"{w.Workspace,-30} {w.Chunks,10} {w.Edges,10} {when,-25}");
        if (w.ByLanguage.Count > 0)
            Console.WriteLine($"   languages: {string.Join(", ", w.ByLanguage.Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (w.ByProject.Count > 0)
            Console.WriteLine($"   projects:  {string.Join(", ", w.ByProject.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }
}

async Task DropWorkspace(string[] args)
{
    var (positional, _) = ParseArgs(args, 1);
    if (positional.Count == 0)
    {
        Console.Error.WriteLine("Usage: drop-workspace <name>");
        return;
    }
    var workspace = positional[0];
    var store = sp.GetRequiredService<IVectorStore>();
    Console.WriteLine($"Deleting all chunks and edges for workspace '{workspace}'...");
    await store.DeleteByWorkspaceAsync(workspace);
    Console.WriteLine("Done.");
}

void PrintStats(IndexingStats stats)
{
    Console.WriteLine($"\nIndexing complete in {stats.Duration.TotalSeconds:F1}s");
    if (!string.IsNullOrEmpty(stats.Workspace))
        Console.WriteLine($"  Workspace:       {stats.Workspace}");
    Console.WriteLine($"  Total chunks:    {stats.TotalChunks}");
    Console.WriteLine($"  Methods:         {stats.Methods}");
    Console.WriteLine($"  Classes:         {stats.Classes}");
    Console.WriteLine($"  Properties:      {stats.Properties}");
    Console.WriteLine($"  Edges (total):   {stats.Edges}");
    Console.WriteLine($"    internal:      {stats.InternalEdges}");
    Console.WriteLine($"    external:      {stats.ExternalEdges}");
    Console.WriteLine($"  Library calls:   {stats.LibraryCalls}");
    foreach (var (lang, count) in stats.ByLanguage)
        Console.WriteLine($"  [{lang}]:  {count}");
}

void PrintUsage()
{
    Console.WriteLine("""
    CodeRag - Code Analysis RAG Indexer

    Usage:
      coderag init
          Create database tables.

      coderag index-solution <path.sln> --workspace <name>
          Index a C# solution (Roslyn, full semantic) under a workspace.

      coderag index-dir <path> --workspace <name> [--project <name>]
          Index a source directory under a workspace, optional sub-project tag.

      coderag query <terms> [--workspace <name>] [--all-workspaces] [options]
          Search the index. Defaults workspace from CODERAG_DEFAULTWORKSPACE
          or 'DefaultWorkspace' in appsettings.json. Use --all-workspaces to
          search across every workspace.

      coderag list-workspaces
          Show every workspace with chunk/edge counts and last-indexed time.

      coderag drop-workspace <name>
          Delete all chunks and edges for a workspace.

      coderag stats
          Global counts grouped by workspace, language, kind, project.

    Query options:
      --top N                Number of results (default: 10)
      --lang <language>      Filter by language (csharp, python, typescript...)
      --kind <kind>          Filter by kind (method_declaration, class_declaration...)
      --project <name>       Filter by inner project name
      --workspace <name>     Restrict to one workspace
      --all-workspaces       Search across every workspace
      --expand               Also show callers/callees (graph neighbors)

    Environment variables:
      CODERAG_CONNECTIONSTRING     Postgres connection string
      CODERAG_OPENAIAPI_KEY        OpenAI API key for embeddings
      CODERAG_EMBEDDINGMODEL       Model name (default: text-embedding-3-small)
      CODERAG_EMBEDDINGDIMENSIONS  Vector dimensions (default: 1536)
      CODERAG_DEFAULTWORKSPACE     Default workspace for query if not given
    """);
}

// Tiny argument parser: separates positional args from --flag / --flag value pairs.
static (List<string> positional, Dictionary<string, string> opts) ParseArgs(string[] args, int startIndex)
{
    var positional = new List<string>();
    var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (int i = startIndex; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith("--"))
        {
            var name = a[2..];
            // value follows if next token is not another --flag
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                opts[name] = args[++i];
            }
            else
            {
                opts[name] = "true";
            }
        }
        else
        {
            positional.Add(a);
        }
    }

    return (positional, opts);
}
