# CodeRag

A hybrid **vector + call-graph** code index for RAG. It extracts classes, methods, properties, library calls, and call-graph edges from source code, embeds them, and stores everything in PostgreSQL/pgvector for semantic search with structural expansion.

## Architecture

```
CodeRag.Core          Models, interfaces (IVectorStore, ILanguageAnalyzer, IEmbeddingService)
CodeRag.Analyzers     Roslyn (C#, full semantic) + Tree-sitter stubs (Python, TypeScript, Go)
CodeRag.Storage       EF Core + PostgreSQL/pgvector, OpenAI embeddings
CodeRag.Cli           Command-line interface
```

## Concepts

### Workspaces

A **workspace** is a logical grouping — typically one solution, repo, or monorepo — used to keep indexes isolated.

- Every chunk and edge is tagged with a workspace.
- Edge resolution (caller → callee) is **scoped to a single indexing run**, so two workspaces with identical method signatures never cross-link.
- Workspaces are first-class for the upcoming dashboard / AI agent: `list-workspaces` enumerates them, `drop-workspace` cleanly removes one.

A workspace is **distinct from an inner `ProjectName`** (the `.csproj` name inside a solution). One workspace usually contains several projects.

### Call graph

In addition to vector chunks, the indexer extracts edges:

| Edge kind    | Meaning                                  |
|--------------|------------------------------------------|
| `calls`      | method A invokes method B                |
| `creates`    | method A constructs type B               |
| `inherits`   | type A inherits from base type B         |
| `implements` | type A implements interface B            |

Edges are resolved against canonical Roslyn signatures (`IMethodSymbol.OriginalDefinition.ToDisplayString()`) within the workspace. Each chunk also stores a `CallerIds` list so you can walk the graph in either direction.

Use `--expand` on a query to pull in callers/callees of the top hit.

## What Gets Indexed

| Element            | Fields Stored                                                                  |
|--------------------|--------------------------------------------------------------------------------|
| Classes/Structs    | Name, namespace, modifiers, attributes, XML doc, file, line, base/interface ref|
| Methods            | Signature, parameters, return type, body, XML doc, modifiers, callers/callees  |
| Constructors       | Parameters, body, XML doc                                                      |
| Properties         | Name, type, modifiers, XML doc                                                 |
| Enums              | Name, members, XML doc                                                         |
| Library calls      | Assembly, namespace, signature, call location                                  |
| Edges              | Source chunk → target signature, kind (calls/creates/inherits/implements)      |

Each element is embedded as a vector and stored for similarity search.

## Quick Start

### 1. Start PostgreSQL with pgvector

```bash
docker compose up -d
```

### 2. Set your OpenAI API key

```bash
# bash
export CODERAG_OPENAIAPI_KEY="sk-..."
```

```powershell
# PowerShell
$env:CODERAG_OPENAIAPI_KEY = "sk-..."
```

Or edit `src/CodeRag.Cli/appsettings.json`.

### 3. Build and initialize

```bash
dotnet build
cd src/CodeRag.Cli

# Create database tables
dotnet run -- init

# Index a C# solution (full Roslyn semantic analysis)
dotnet run -- index-solution /path/to/MySolution.sln --workspace MyApp

# Or index any source directory
dotnet run -- index-dir /path/to/source --workspace MyApp --project MyApp.Web
```

`--workspace <name>` is **required** for both index commands.

### 4. Query

```bash
# Free-text semantic search
dotnet run -- query "authentication middleware" --workspace MyApp

# Expand the top hit with its callers and callees
dotnet run -- query "token validation" --workspace MyApp --expand

# Filters
dotnet run -- query "database connection" --workspace MyApp --lang csharp --kind method_declaration --top 5
dotnet run -- query "error handling"      --workspace MyApp --project MyApp.Web

# Cross-workspace search (opt-in)
dotnet run -- query "retry policy" --all-workspaces

# Discover workspaces
dotnet run -- list-workspaces

# Wipe one
dotnet run -- drop-workspace MyApp

# Index stats (totals + per-workspace breakdown)
dotnet run -- stats
```

#### Default workspace at query time

When `--workspace` is omitted, the CLI resolves it in this order:

1. `--workspace <name>` argument
2. `CODERAG_DEFAULTWORKSPACE` environment variable
3. `DefaultWorkspace` in `appsettings.json`
4. Otherwise: error (use `--all-workspaces` to opt out of isolation)

## Configuration

| Env Variable                    | appsettings.json Key   | Default                                |
|---------------------------------|------------------------|----------------------------------------|
| `CODERAG_CONNECTIONSTRING`      | `ConnectionString`     | `Host=localhost;Database=coderag;...`  |
| `CODERAG_OPENAIAPI_KEY`         | `OpenAiApiKey`         | (none — uses fake embeddings)          |
| `CODERAG_EMBEDDINGMODEL`        | `EmbeddingModel`       | `text-embedding-3-small`               |
| `CODERAG_EMBEDDINGDIMENSIONS`   | `EmbeddingDimensions`  | `1536`                                 |
| `CODERAG_DEFAULTWORKSPACE`      | `DefaultWorkspace`     | (none)                                 |

## CLI Reference

```
coderag init
coderag index-solution <path.sln> --workspace <name>
coderag index-dir       <path>     --workspace <name> [--project <name>]
coderag query           <terms>   [--workspace <name>] [--all-workspaces]
                                  [--top N] [--lang <l>] [--kind <k>]
                                  [--project <name>] [--expand]
coderag list-workspaces
coderag drop-workspace  <name>
coderag stats
```

## Swapping the Vector Store

`IVectorStore` abstracts the database. To use Qdrant, ChromaDB, or another backend:

1. Implement `IVectorStore` (chunks, edges, workspace ops).
2. Replace the `AddPgVectorStore()` call in DI setup with your own registration.

Key methods: `InitializeAsync`, `UpsertAsync` (chunks), `UpsertEdgesAsync`, `SearchAsync`, `GetCallersAsync` / `GetCalleesAsync`, `DeleteByFileAsync` / `DeleteByProjectAsync` / `DeleteByWorkspaceAsync`, `ListWorkspacesAsync`, `GetStatsAsync`.

## Adding Languages

1. Implement `ILanguageAnalyzer` (or extend `TreeSitterAnalyzerBase`).
2. Register it: `services.AddSingleton<ILanguageAnalyzer, YourAnalyzer>()`.
3. The indexer auto-routes files by extension.

Tree-sitter stubs for Python, TypeScript, and Go are ready — add the NuGet packages and implement the parsing logic.

## Database Schema

Two tables, both partitioned logically by `workspace` (indexed).

### `code_chunks`

```
-- Identity
id, workspace, kind, language, namespace, class_name, function_name, signature

-- Location
file_path, line_number, end_line_number

-- Content
documentation, body, body_summary

-- Library tracking
library_assembly, library_package

-- Metadata
project_name, return_type, modifiers[], parameters[], attributes[], caller_ids[]

-- Vector
embedding vector(1536)
```

Indexed on: `workspace`, `language`, `kind`, `project_name`, `file_path`, `class_name`, and `embedding` (HNSW/IVFFlat).

### `code_edges`

```
id, workspace, source_chunk_id, target_chunk_id, target_signature, kind,
file_path, line_number, project_name
```

Indexed on: `workspace`, `source_chunk_id`, `target_chunk_id`, `target_signature`, `kind`.

## Notes

- Schema is created via `EnsureCreatedAsync` — there are no EF migrations yet. After schema changes, recreate the DB:

  ```bash
  docker compose down -v
  docker compose up -d
  dotnet run --project src/CodeRag.Cli -- init
  ```
- Embeddings fall back to a deterministic fake vector when no OpenAI key is set — useful for smoke tests, not for real search.
