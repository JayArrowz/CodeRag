import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { Api } from "./api/Api.js";

const baseURL = process.env.CODERAG_URL ?? "http://localhost:5180";
const client = new Api({ baseURL });

const server = new McpServer({
  name: "mcp-coderag",
  version: "1.0.1",
});

server.registerTool(
  "coderag_list_workspaces",
  {
    title: "List Workspaces",
    description:
      "List all indexed CodeRag workspaces with their chunk and edge counts. " +
      "Call this first to discover available workspace names before querying.",
    inputSchema: {},
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async () => {
    try {
      const res = await client.api.workspacesList();
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error listing workspaces: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

server.registerTool(
  "coderag_type_implementors",
  {
    title: "Type Implementors",
    description:
      "Return all types that directly implement or inherit from the given signature. " +
      "Use this to explore class hierarchies or find all concrete implementations of an interface.",
    inputSchema: {
      signature: z.string().describe("Fully-qualified type signature to look up (e.g. 'CodeRag.Core.Interfaces.IVectorStore')"),
      workspace: z.string().optional().describe("Limit results to a specific workspace; omit to search all workspaces"),
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async (params) => {
    try {
      const res = await client.api.typesImplementorsList({
        signature: params.signature,
        workspace: params.workspace,
      });
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error fetching implementors: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

server.registerTool(
  "coderag_chunk_edges",
  {
    title: "Chunk Edges",
    description:
      "Return the full call-graph edges (incoming and outgoing) for a specific chunk ID. " +
      "Use this to answer 'who calls this method?' or 'what does this method call?' " +
      "after obtaining a chunkId from a prior query result.",
    inputSchema: {
      chunkId: z.string().uuid().describe("The chunk ID (UUID) from a prior query result"),
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async (params) => {
    try {
      const [outgoing, incoming] = await Promise.all([
        client.api.chunksEdgesOutgoingList(params.chunkId),
        client.api.chunksEdgesIncomingList(params.chunkId),
      ]);
      return {
        content: [{ type: "text", text: JSON.stringify({ incoming: incoming.data, outgoing: outgoing.data }, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error fetching chunk edges: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

server.registerTool(
  "coderag_list_files",
  {
    title: "List Indexed Files",
    description:
      "List all files indexed in a workspace, with path, chunk count, and last-indexed timestamp. " +
      "Use this to browse the project structure and discover what files exist in the index " +
      "before fetching their chunks or querying specific paths.",
    inputSchema: {
      workspace: z.string().describe("Workspace name to list files for"),
      project: z.string().optional().describe("Optional project name to narrow the listing"),
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async (params) => {
    try {
      const res = await client.api.filesList({ workspace: params.workspace, project: params.project });
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error listing files: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

server.registerTool(
  "coderag_bulk_query",
  {
    title: "Bulk Query",
    description:
      "Run multiple codebase queries in parallel and return all results in one call. " +
      "Use this instead of repeated coderag_query calls when you need context for several " +
      "topics or symbols at once. Results are grouped by query. " +
      "All queries share the same workspace/filter settings.",
    inputSchema: {
      queries: z.array(z.string()).min(1).max(10).describe("List of queries to run in parallel (max 10)"),
      workspace: z.string().optional().describe("Limit all queries to this workspace"),
      workspaces: z.array(z.string()).optional().describe("Limit all queries to these workspaces"),
      language: z.string().optional().describe("Filter by language for all queries"),
      kind: z.string().optional().describe(
        "Filter by chunk kind. Canonical values: " +
        "'class_declaration', 'interface_declaration', 'method_declaration', 'constructor_declaration', " +
        "'function_declaration', 'property_declaration', 'field_declaration', " +
        "'enum_declaration', 'record_declaration' (C# only), 'struct_declaration' (C# only), " +
        "'type_alias_declaration' (TypeScript/JS only)"
      ),
      filePath: z.string().optional().describe("Only include chunks whose file path contains this substring, e.g. 'src/Services'"),
      excludePaths: z.array(z.string()).optional().describe("Exclude chunks whose file path contains any of these substrings, e.g. ['tests/', 'examples/']"),
      project: z.string().optional().describe("Filter all queries to a specific project name"),
      topK: z.number().int().optional().describe("Results per query (default: 8)"),
      expandNeighbors: z.boolean().optional().describe("Include containing type and callers (default: true)"),
      hydrateEdges: z.boolean().optional().describe("Include external library docs (default: true)"),
      minVectorScore: z.number().optional().describe("Minimum cosine similarity threshold (0-1)"),
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async (params) => {
    try {
      const results = await Promise.all(
        params.queries.map((q) =>
          client.api.queryCreate({
            query: q,
            workspace: params.workspace,
            workspaces: params.workspaces,
            language: params.language,
            kind: params.kind,
            filePath: params.filePath,
            excludeFilePathContains: params.excludePaths,
            project: params.project,
            topK: params.topK ?? 8,
            expandNeighbors: params.expandNeighbors ?? true,
            hydrateEdges: params.hydrateEdges ?? true,
            retrievalText: true,
            minVectorScore: params.minVectorScore,
          })
        )
      );
      const grouped = params.queries.map((q, i) => ({ query: q, results: results[i].data }));
      return {
        content: [{ type: "text", text: JSON.stringify(grouped, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error in bulk query: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

server.registerTool(
  "coderag_bulk_type_members",
  {
    title: "Bulk Type Members",
    description:
      "Fetch members of multiple types in parallel. " +
      "Use this after coderag_type_implementors returns several types and you need " +
      "all their members at once, instead of looping with coderag_type_members.",
    inputSchema: {
      types: z
        .array(
          z.object({
            workspace: z.string().describe("Workspace name"),
            className: z.string().describe("Class or interface name"),
            namespace: z.string().optional().describe("Optional namespace to disambiguate"),
          })
        )
        .min(1)
        .max(20)
        .describe("List of types to fetch members for (max 20)"),
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async (params) => {
    try {
      const results = await Promise.all(
        params.types.map((t) =>
          client.api.typesMembersList({ workspace: t.workspace, className: t.className, namespace: t.namespace })
        )
      );
      const grouped = params.types.map((t, i) => ({ ...t, members: results[i].data }));
      return {
        content: [{ type: "text", text: JSON.stringify(grouped, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error in bulk type members: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

server.registerTool(
  "coderag_bulk_file_chunks",
  {
    title: "Bulk File Chunks",
    description:
      "Fetch chunk outlines for multiple files in parallel. " +
      "Use this to get an overview of several files at once instead of " +
      "calling coderag_file_chunks repeatedly.",
    inputSchema: {
      files: z
        .array(
          z.object({
            path: z.string().describe("Relative file path as stored in the index"),
            workspace: z.string().describe("Workspace name"),
          })
        )
        .min(1)
        .max(20)
        .describe("List of files to fetch chunks for (max 20)"),
    },
    annotations: {
      readOnlyHint: true,
      destructiveHint: false,
      idempotentHint: true,
      openWorldHint: false,
    },
  },
  async (params) => {
    try {
      const results = await Promise.all(
        params.files.map((f) => client.api.filesChunksList({ path: f.path, workspace: f.workspace }))
      );
      const grouped = params.files.map((f, i) => ({ ...f, chunks: results[i].data }));
      return {
        content: [{ type: "text", text: JSON.stringify(grouped, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error in bulk file chunks: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`CodeRag MCP server running (CODERAG_URL=${baseURL})`);
