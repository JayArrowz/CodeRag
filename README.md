# CodeRag

A hybrid **vector + call-graph** code index for RAG. It extracts classes, methods, properties, library calls, and call-graph edges from C# source code, embeds them, and stores everything in PostgreSQL/pgvector for semantic and structural search. A Blazor Server dashboard provides live indexing, interactive exploration, and semantic search.

## Architecture

```
CodeRag.Core          Models, interfaces (IVectorStore, ILanguageAnalyzer, IEmbeddingService, ISolutionAnalyzer)
CodeRag.Analyzers     Roslyn (C#, full semantic) + Tree-sitter stubs (Python, TypeScript, Go)
CodeRag.Storage       EF Core + PostgreSQL/pgvector, OpenAI embeddings
CodeRag.Dashboard     Blazor Server dashboard -- indexing, search, explorer, watches
```

## Concepts

### Workspaces

A **workspace** is a logical grouping -- typically one solution, repo, or monorepo -- used to keep indexes isolated.

- Every chunk and edge is tagged with a workspace.
- Edge resolution (caller -> callee) is scoped within the workspace so two workspaces with identical method signatures never cross-link.
- Workspaces can be **closed** (watches disabled, Roslyn cache freed) and **re-opened**, or **dropped** (all chunks/edges deleted).

A workspace is **distinct from a `ProjectName`** (the `.csproj` name inside a solution). One workspace usually contains several projects.

### Call graph

In addition to vector chunks, the indexer extracts directed edges:

| Edge kind    | Meaning                                 |
|--------------|-----------------------------------------|
| `calls`      | method A invokes method B               |
| `creates`    | method A constructs type B              |
| `inherits`   | type A inherits from base type B        |
| `implements` | type A implements interface B           |

Edges are resolved against canonical Roslyn signatures within the workspace. Unresolved edges (target indexed in a different run) are lazily resolved at query time and persisted so subsequent lookups are instant.

### Query pipeline

`CodebaseIndexer.QueryAsync` runs a multi-stage hybrid retrieval:

1. **Symbol match** -- exact identifier lookup, pinned at the top (optional).
2. **Vector ANN** -- embedding similarity search over the candidate pool.
3. **Lexical search** -- full-text match over names, signatures, docs, and paths.

Stages 1-3 run **concurrently**. Results are then fused with **Reciprocal Rank Fusion**, pruned by a minimum vector score, and diversity-capped (per-file and per-class limits). An optional **neighborhood expansion** step adds the containing type and incoming callers for each top result, also run in parallel. Outgoing edges can be hydrated in a parallel pass so AI context includes external-library docs.

Every stage is individually toggleable via `QueryOptions`.

## What Gets Indexed

| Element         | Fields Stored                                                                   |
|-----------------|---------------------------------------------------------------------------------|
| Classes/Structs | Name, namespace, modifiers, attributes, XML doc, file, line, base/interface ref |
| Methods         | Signature, parameters, return type, body, XML doc, modifiers, callers/callees   |
| Constructors    | Parameters, body, XML doc                                                       |
| Properties      | Name, type, modifiers, XML doc                                                  |
| Enums           | Name, members, XML doc                                                          |
| Library calls   | Assembly, namespace, signature, call location                                   |
| Edges           | Source chunk -> target signature/chunk, kind (calls/creates/inherits/implements) |

## Quick Start

### 1. Start the database

**PostgreSQL** (recommended):

```bash
docker compose up -d
```

**SQLite** (zero setup): set `Database.Provider` to `Sqlite` and `Database.ConnectionString` to `Data Source=coderag.db` in `appsettings.json` -- no Docker needed.

### 2. Configure an embedding provider

Edit `src/CodeRag.Dashboard/appsettings.json` (or use environment variables):

**Google (Gemini):**
```json
"Embedding": { "Provider": "Google", "ApiKey": "AIza...", "Model": "models/gemini-embedding-001", "Dimensions": 3072 }
```

**OpenAI:**
```json
"Embedding": { "Provider": "OpenAI", "ApiKey": "sk-...", "Model": "text-embedding-3-small", "Dimensions": 1536 }
```

Without an API key the app starts with fake embeddings (vector search returns nothing useful but the rest of the UI works).

### 3. Run the dashboard

```bash
dotnet run --project src/CodeRag.Dashboard
```

The database schema is created automatically on first run. Open `https://localhost:5001` in your browser.

### 4. Index your code

Navigate to **Index** in the sidebar and either:

- **Index a solution** -- provide the path to a `.sln` or `.slnx` file and a workspace name. Uses full Roslyn semantic analysis (cross-file call edges, type resolution).
- **Index a directory** -- provide any source directory path, workspace name, and optional project name. Uses fast structure-only analysis.

After indexing completes the job page shows stats and a `FileSystemWatcher` is automatically registered for the indexed path so future file changes are reindexed incrementally.

## Dashboard

### Pages

| Page | Route | Description |
|---|---|---|
| **Overview** | `/` | Total chunk/edge counts, per-workspace summary, links to all sections |
| **Workspaces** | `/workspaces` | List all workspaces with chunk/edge stats |
| **Workspace detail** | `/workspaces/{name}` | Stats breakdown by language and project; **Close**, **Open**, and **Drop** actions |
| **Search** | `/search` | Hybrid semantic search with configurable pipeline options; results link to Explorer |
| **Explorer** | `/explore/{workspace}` | Interactive tree (project -> namespace -> class -> member) with call graph detail panel; supports `?chunk={guid}` URL navigation |
| **Index** | `/index` | Kick off a solution or directory index job |
| **Watches** | `/watches` | Manage live file-system watches; add watches manually or view/edit those created by index jobs |
| **Jobs** | `/jobs` | Browse background indexing jobs |
| **Job detail** | `/jobs/{id}` | Live console output and stats for a running or completed job |

### Watches

A **watch** is a directory that is automatically reindexed when files change. Watches are persisted to `%LOCALAPPDATA%/CodeRag/watches.json` (overridable via `WatchesFile` config key) and survive app restarts.

- Watches are created automatically after a successful index job.
- For **solution-level** jobs, one watch is created per project directory with the solution path stored -- file changes are then reindexed using the full Roslyn semantic model (preserving cross-file call edges).
- For **directory-level** jobs, a single watch is created for the directory.
- A **debounce window** (750 ms) coalesces rapid saves, git checkouts, and build output bursts before reindexing.
- On startup, a **catch-up sweep** re-indexes any files modified while the dashboard was offline.
- Watches can also be added manually from the Watches page, including an optional **solution path** to enable Roslyn-semantic incremental reindex.

### Workspace lifecycle

| Action | Effect |
|--------|--------|
| **Close** | Disables all watches, detaches `FileSystemWatcher`s, evicts Roslyn's `MSBuildWorkspace` cache. Chunks/edges and watch records are preserved. |
| **Open** | Re-enables watches, re-attaches watchers, runs a catch-up sweep. |
| **Drop** | Closes the workspace first, then permanently deletes all chunks and edges from the database. |

### Explorer URL navigation

The Explorer supports deep-linking via `?chunk={guid}`. Navigating to `/explore/MyApp?chunk=<id>` will load the workspace, select that chunk in the tree (auto-expanding the project -> namespace -> class path), and show its detail panel. All call-graph entries and member rows are rendered as `<a href>` links for easy bookmarking.

## Configuration

All settings live under two JSON sections in `appsettings.json`. Every key can be overridden at runtime by a `CODERAG_` prefixed environment variable using double-underscore `__` as section separator (e.g. `CODERAG_Embedding__ApiKey`).

### Database

```json
"Database": {
  "Provider": "Postgres",
  "ConnectionString": "Host=localhost;Database=coderag;Username=postgres;Password=..."
}
```

| `Provider` value | Backend | Notes |
|------------------|---------|-------|
| `Postgres` | PostgreSQL + pgvector | Recommended for production. Requires the `vector` extension. |
| `Sqlite` | SQLite + sqlite-vec | Zero-setup, single-file DB. Use `Data Source=coderag.db` as the connection string. |

### Embedding

```json
"Embedding": {
  "Provider": "Google",
  "ApiKey": "AIza...",
  "Model": "models/gemini-embedding-001",
  "Dimensions": 3072
}
```

| `Provider` value | Default model | Default dims | Notes |
|------------------|---------------|--------------|-------|
| `OpenAI` | `text-embedding-3-small` | 1536 | Set `BaseUrl` to override the endpoint (Azure OpenAI, local proxy, etc.) |
| `Google` | `text-embedding-004` | 3072 | Uses Gemini Embedding API. `models/gemini-embedding-001` also works (3072 dims). |

`Dimensions` can be left at `0` to use the provider default. When no `ApiKey` is set, a deterministic fake embedding service is used (useful for smoke tests, not for real search).

### Other settings

| appsettings.json key | Default | Description |
|----------------------|---------|-------------|
| `WatchesFile` | `%LOCALAPPDATA%/CodeRag/watches.json` | Path to the file-watch persistence store |

## Swapping the Vector Store

`IVectorStore` abstracts the database. To use Qdrant, ChromaDB, or another backend:

1. Implement `IVectorStore` (chunks, edges, workspace ops).
2. Register it in `VectorStoreServiceCollectionExtensions.AddVectorStore` or replace the call in DI setup directly.

Key methods: `InitializeAsync`, `UpsertAsync` (chunks), `UpsertEdgesAsync`, `SearchAsync`, `ExactSymbolSearchAsync`, `LexicalSearchAsync`, `GetCallersAsync` / `GetCalleesAsync` / `GetOutgoingEdgesAsync`, `DeleteByFileAsync` / `DeleteByProjectAsync` / `DeleteByWorkspaceAsync`, `ListWorkspacesAsync`, `GetStatsAsync`.

## Adding Languages

1. Implement `ILanguageAnalyzer` (or extend `TreeSitterAnalyzerBase`).
2. Register it: `services.AddSingleton<ILanguageAnalyzer, YourAnalyzer>()`.
3. The indexer auto-routes files by extension.

Tree-sitter stubs for Python, TypeScript, and Go are in place -- add the NuGet packages and implement the parsing logic.

## Database Schema

Two tables, both logically partitioned by `workspace` (indexed).

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
id, workspace, source_chunk_id, target_chunk_id, target_signature, source_signature,
kind, file_path, line_number, project_name, is_external
```

Indexed on: `workspace`, `source_chunk_id`, `target_chunk_id`, `target_signature`, `kind`.

## Notes

- Schema is created via `EnsureCreatedAsync` -- there are no EF migrations. After schema changes, recreate the DB:

  ```bash
  docker compose down -v
  docker compose up -d
  dotnet run --project src/CodeRag.Dashboard
  ```
- Embeddings fall back to a deterministic fake vector when no API key is set -- useful for smoke tests, not for real search.
- `TargetChunkId` on edges may be `null` when the callee was indexed in a different run. The Explorer lazily resolves these at query time and persists the result so subsequent lookups are instant.