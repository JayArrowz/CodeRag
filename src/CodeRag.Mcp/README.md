
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

The server connects to the CodeRag dashboard API. Set `CODERAG_URL` to point at your running dashboard (defaults to `http://localhost:5180` or port 7180 via docker):

**VS Code (`settings.json`):**
```json
"mcp": {
  "servers": {
    "coderag": {
      "command": "npx",
      "args": ["-y", "@jayarrowz/mcp-coderag"],
      "env": { "CODERAG_URL": "http://localhost:7180" }
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
    "env": { "CODERAG_URL": "http://localhost:7180" }
  }
}
```

The source lives in `src/CodeRag.Mcp/`. See the [npm package](https://www.npmjs.com/package/@jayarrowz/mcp-coderag) for the latest release.
