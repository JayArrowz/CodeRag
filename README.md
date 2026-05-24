# CodeRag

A hybrid **vector + call-graph** code index for RAG. It extracts classes, methods, properties, library calls, and call-graph edges from C# and TypeScript/TSX source code, embeds them, and stores everything in PostgreSQL/pgvector for semantic and structural search. A Blazor Server dashboard provides live indexing, interactive exploration, and semantic search.

<img width="2886" height="2067" alt="image" src="https://github.com/user-attachments/assets/c55b2a1e-75a6-4766-8706-359ec925d05b" />

<img width="1719" height="2012" alt="image" src="https://github.com/user-attachments/assets/4d45fe3d-e6fc-4c31-99b6-e39c8a62b4e6" />


## Architecture

```
CodeRag.Core          Models, interfaces (IVectorStore, ILanguageAnalyzer, IEmbeddingService, ISolutionAnalyzer)
CodeRag.Analyzers     Roslyn (C#, full semantic) + TsCompilerAnalyzer (TS/TSX, full type-checker)
                      + Tree-sitter stubs (JavaScript/JSX, Python, Go)
CodeRag.Storage       EF Core + PostgreSQL/pgvector, OpenAI / Google / Ollama embeddings
CodeRag.Dashboard     Blazor Server dashboard -- indexing, search, explorer, watches
tools/ts-analyzer     Node.js sidecar (ts-morph) spawned by TsCompilerAnalyzer
```

## Supported Languages

| Language | Analyzer | Semantic edges | Notes |
|----------|----------|----------------|-------|
| C# | Roslyn (`MSBuildWorkspace`) | Full — calls, creates, inherits, implements | Requires a `.sln` / `.csproj` descriptor |
| TypeScript / TSX | `TsCompilerAnalyzer` + Node.js sidecar | Full — calls, creates, inherits, implements, renders, passes | Requires Node.js 18+; `tsconfig.json` auto-discovered |
| JavaScript / JSX | `JavaScriptAnalyzer` (tree-sitter) | Structural only | No type resolution |
| Python, Go | Tree-sitter stubs | Structural only | Extend `TreeSitterAnalyzerBase` |

## Concepts

### Workspaces

A **workspace** is a logical grouping -- typically one solution, repo, or monorepo -- used to keep indexes isolated.

- Every chunk and edge is tagged with a workspace.
- Edge resolution (caller -> callee) is scoped within the workspace so two workspaces with identical method signatures never cross-link.
- Workspaces can be **closed** (watches disabled, Roslyn cache freed) and **re-opened**, or **dropped** (all chunks/edges deleted).

A workspace is **distinct from a `ProjectName`** (the `.csproj` name inside a solution). One workspace usually contains several projects.

### Call graph

In addition to vector chunks, the indexer extracts directed edges:

| Edge kind    | Languages         | Meaning                                        |
|--------------|-------------------|------------------------------------------------|
| `calls`      | C#, TS/TSX        | method / function A invokes B                  |
| `creates`    | C#, TS/TSX        | method A constructs type B (`new`)             |
| `inherits`   | C#, TS/TSX        | type A inherits from base type B               |
| `implements` | C#, TS/TSX        | type A implements interface B                  |
| `renders`    | TSX               | component A renders component B in JSX         |
| `passes`     | TSX               | component A passes a symbol as a prop to B     |

Edges are resolved against canonical signatures within the workspace. Unresolved edges (target indexed in a different run) are lazily resolved at query time and persisted so subsequent lookups are instant.

### Query pipeline

`CodebaseIndexer.QueryAsync` runs a multi-stage hybrid retrieval:

1. **Symbol match** -- exact identifier lookup, pinned at the top (optional).
2. **Vector ANN** -- embedding similarity search over the candidate pool.
3. **Lexical search** -- full-text match over names, signatures, docs, and paths.

Stages 1-3 run **concurrently**. Results are then fused with **Reciprocal Rank Fusion**, pruned by a minimum vector score, and diversity-capped (per-file and per-class limits). An optional **neighborhood expansion** step adds the containing type and incoming callers for each top result, also run in parallel. Outgoing edges can be hydrated in a parallel pass so AI context includes external-library docs.

Every stage is individually toggleable via `QueryOptions`.

## What Gets Indexed

| Element                      | Languages  | Fields Stored                                                                    |
|------------------------------|------------|-----------------------------------------------------------------------------------|
| Classes / interfaces         | C#, TS/TSX | Name, namespace, modifiers, attributes, doc, file, line, base/interface refs      |
| Methods / functions          | C#, TS/TSX | Signature, parameters, return type, body, doc, modifiers, callers/callees          |
| Constructors                 | C#, TS/TSX | Parameters, body, doc                                                             |
| Properties / fields          | C#, TS/TSX | Name, type, modifiers, doc                                                        |
| Arrow functions / `const fn` | TS/TSX     | Inlined as `function_declaration` chunks with full signature                      |
| Type aliases                 | TS/TSX     | Name, namespace, body                                                             |
| Enums                        | C#         | Name, members, XML doc                                                            |
| Library calls                | C#, TS/TSX | Assembly, namespace, signature, call location                                     |
| Edges                        | C#, TS/TSX | Source → target signature/chunk, kind (calls/creates/inherits/implements/renders/passes) |

## Quick Start

### Option A — Docker Compose (recommended)

Runs the dashboard **and** PostgreSQL together with a single command. No local .NET or Node.js install needed.

**1. Copy the example env file and fill in your embedding API key:**

```bash
cp .env.example .env
# edit .env and set CODERAG_Embedding__ApiKey
```

**2. Build and start everything:**

```bash
docker compose up -d --build
```

> **Using Ollama for embeddings?** Enable the `ollama` profile so the Ollama server and model pull run alongside the dashboard:
> ```bash
> # in .env
> COMPOSE_PROFILES=ollama
> CODERAG_Embedding__Provider=Ollama
> CODERAG_Embedding__Model=qwen3-embedding
> CODERAG_Embedding__BaseUrl=http://ollama:11434
> ```
> The `ollama-pull` service automatically pulls the configured model on first start. Model files are stored at `OLLAMA_DATA_PATH` (default: `./ollama-data`).
>
> **GPU support:** by default Ollama runs CPU-only. Add the appropriate override for your GPU:
> - **NVIDIA**: `docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d --build` (requires [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html))
> - **AMD**: `docker compose -f docker-compose.yml -f docker-compose.amd.yml up -d --build`
> - **Intel**: `docker compose -f docker-compose.yml -f docker-compose.intel.yml up -d --build`

The first build takes a few minutes (restores NuGet packages, runs `npm ci`). Subsequent starts are instant.

**3. Open the dashboard:**

```
http://localhost:5180
```

> **Indexing paths**: the host directory set by `WORKSPACE_PATH` in your `.env` file (default: the repo root) is mounted read-write at `/workspace` inside the container. All paths entered in the dashboard must use this prefix — e.g. `/workspace/myapp` maps to `$WORKSPACE_PATH/myapp` on the host.

**Tear down** (keeps data volumes):
```bash
docker compose down
```

**Full reset** (destroys all indexed data):
```bash
docker compose down -v
```

---

### Option B — Local (bare metal)

### 1. Start the database

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

**Ollama:**
```json
"Embedding": { "Provider": "Ollama", "Model": "qwen3-embedding", "Dimensions": 3072, "BaseUrl": "http://localhost:11434" }
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

A **watch** is a directory that is automatically reindexed when files change. Watches are persisted to a JSON file and survive app restarts.

- **Local / bare-metal**: stored at `%LOCALAPPDATA%/CodeRag/watches.json` by default.
- **Docker**: stored at `/data/watches.json` inside the container, backed by the `watches-data` named volume declared in `docker-compose.yml`. This ensures watches are not lost when the container is restarted or replaced.
- Override the path via the `WatchesFile` config key (or `CODERAG_WatchesFile` env var).

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
| `Ollama` | _(none)_ | _(model-specific)_ | Set `BaseUrl` to the Ollama server (e.g. `http://localhost:11434`). When using Docker Compose, use `http://ollama:11434` and enable `COMPOSE_PROFILES=ollama`. The first embedding request will be slow while Ollama loads the model into memory; after that the model stays resident and subsequent calls are fast. |

`Dimensions` can be left at `0` to use the provider default. When no `ApiKey` is set, a deterministic fake embedding service is used (useful for smoke tests, not for real search).

### Other settings

| appsettings.json key | Default | Description |
|----------------------|---------|-------------|
| `WatchesFile` | `%LOCALAPPDATA%/CodeRag/watches.json` (local) / `/data/watches.json` (Docker) | Path to the file-watch persistence store. Set via `CODERAG_WatchesFile` env var when running in Docker. |

## Swapping the Vector Store

`IVectorStore` abstracts the database. To use Qdrant, ChromaDB, or another backend:

1. Implement `IVectorStore` (chunks, edges, workspace ops).
2. Register it in `VectorStoreServiceCollectionExtensions.AddVectorStore` or replace the call in DI setup directly.

Key methods: `InitializeAsync`, `UpsertAsync` (chunks), `UpsertEdgesAsync`, `SearchAsync`, `ExactSymbolSearchAsync`, `LexicalSearchAsync`, `GetCallersAsync` / `GetCalleesAsync` / `GetOutgoingEdgesAsync`, `DeleteByFileAsync` / `DeleteByProjectAsync` / `DeleteByWorkspaceAsync`, `ListWorkspacesAsync`, `GetStatsAsync`.

## TypeScript / TSX Support

TypeScript and TSX files are analyzed by a long-lived **Node.js sidecar** process (`tools/ts-analyzer/analyze.js`) that uses [ts-morph](https://ts-morph.com/) to run the full TypeScript type-checker. The .NET `TsCompilerAnalyzer` communicates with it over NDJSON on stdin/stdout.

### Prerequisites

- **Node.js 18+** must be on `PATH` (or available as `node` / `cmd /c node`).
- `npm install` must have been run in `tools/ts-analyzer/` (done automatically on first use, or pre-baked into the Docker image).

### How it works

1. On first use for a workspace, `TsCompilerAnalyzer` spawns `node analyze.js --server` as a background sidecar.
2. An `open` request loads the nearest `tsconfig.json` (auto-discovered from the project directory upward).
3. For a **full index**, an `analyze` request streams all chunks and edges back to .NET.
4. For an **incremental watch update**, a `reanalyze` request passes only the changed file paths; the sidecar refreshes those files from disk and re-emits only the affected chunks/edges while still resolving cross-file type edges against the full project.
5. On workspace deletion, the sidecar session is evicted and the process exits cleanly.

### Running locally without Docker

```bash
cd tools/ts-analyzer
npm install
```

Then index a TypeScript workspace from the dashboard. The sidecar is started automatically.

### Docker

The `Dockerfile` has a dedicated `node-deps` build stage that runs `npm ci --omit=dev`. The runtime image installs Node.js 20 via NodeSource and copies the pre-built `tools/ts-analyzer/node_modules` — no `npm install` is needed at container startup.

## Adding Languages

1. Implement `ILanguageAnalyzer` (or extend `TreeSitterAnalyzerBase`).
2. Register it: `services.AddSingleton<ILanguageAnalyzer, YourAnalyzer>()`.
3. The indexer auto-routes files by extension.

Tree-sitter stubs for JavaScript/JSX, Python, and Go are in place — add the NuGet packages and implement the parsing logic.

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

## MCP / AI Assistant Integration

CodeRag ships an **MCP (Model Context Protocol) server** as an npm package. It exposes the following tools to any MCP-compatible AI assistant (Copilot, Claude, Cursor, etc.):

| Tool | Description |
|------|-------------|
| `coderag_list_workspaces` | List all indexed workspaces and their chunk/edge counts. Call this first to discover workspace names. |
| `coderag_bulk_query` | Run 1–10 hybrid searches in parallel (vector + lexical + symbol, RRF-fused). Returns LLM-ready text blocks including call-graph neighbors and external library XML docs. Prefer this over a single query. |
| `coderag_bulk_file_chunks` | Fetch chunk outlines (all functions, classes, methods) for 1–20 files in parallel. |
| `coderag_bulk_type_members` | Fetch all members of 1–20 types in parallel. Useful after `coderag_type_implementors` to drill into each implementation. |
| `coderag_type_implementors` | Find all types that directly implement or inherit a given signature. |
| `coderag_chunk_edges` | Get incoming and outgoing call-graph edges for a chunk ID. Answers "who calls this?" and "what does this call?" |

### Install

```bash
npm install -g @jayarrowz/mcp-coderag
```

Or run without installing:

```bash
npx @jayarrowz/mcp-coderag
```

### Configure

The server connects to the CodeRag dashboard API. Set `CODERAG_URL` to point at your running dashboard (defaults to `http://localhost:5180`):

**VS Code (`settings.json`):**
```json
"mcp": {
  "servers": {
    "coderag": {
      "command": "npx",
      "args": ["-y", "@jayarrowz/mcp-coderag"],
      "env": { "CODERAG_URL": "http://localhost:5180" }
    }
  }
}
```

**Claude Desktop (`claude_desktop_config.json`):**
```json
"mcpServers": {
  "coderag": {
    "command": "npx",
    "args": ["-y", "@jayarrowz/mcp-coderag"],
    "env": { "CODERAG_URL": "http://localhost:5180" }
  }
}
```

The source lives in `src/CodeRag.Mcp/`. See the [npm package](https://www.npmjs.com/package/@jayarrowz/mcp-coderag) for the latest release.

## Notes

- Schema is created via `EnsureCreatedAsync` -- there are no EF migrations. After schema changes, recreate the DB:

  ```bash
  docker compose down -v
  docker compose up -d
  dotnet run --project src/CodeRag.Dashboard
  ```
- Embeddings fall back to a deterministic fake vector when no API key is set -- useful for smoke tests, not for real search.
- `TargetChunkId` on edges may be `null` when the callee was indexed in a different run. The Explorer lazily resolves these at query time and persists the result so subsequent lookups are instant.
