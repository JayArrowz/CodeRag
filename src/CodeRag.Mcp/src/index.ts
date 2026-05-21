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
  "coderag_query",
  {
    title: "Query Codebase",
    description:
      "Hybrid semantic search over indexed code. Combines vector similarity, " +
      "lexical matching, and exact symbol lookup (fused with RRF). " +
      "Returns code chunks (functions, classes, methods) relevant to the query, " +
      "optionally including call-graph neighbors and outgoing edge documentation. " +
      "Set retrievalText=true to get LLM-ready text blocks instead of raw JSON.",
    inputSchema: {
      query: z.string().describe("Natural-language or symbol query"),
      workspace: z.string().optional().describe("Limit results to a single workspace"),
      workspaces: z.array(z.string()).optional().describe("Limit results to these workspaces"),
      allWorkspaces: z.boolean().optional().describe("Search across all workspaces (default: true when no workspace specified)"),
      language: z.string().optional().describe("Filter by language, e.g. 'csharp' or 'typescript'"),
      kind: z.string().optional().describe("Filter by chunk kind, e.g. 'method_declaration', 'class_declaration'"),
      filePath: z.string().optional().describe("Filter to an exact file path"),
      topK: z.number().int().optional().describe("Maximum results to return (default: 10)"),
      expandNeighbors: z.boolean().optional().describe("Include containing type and callers for each result"),
      hydrateEdges: z.boolean().optional().describe("Include external library documentation for outgoing edges"),
      retrievalText: z.boolean().optional().describe("Return LLM-ready text blocks instead of raw JSON"),
      enableSymbolMatch: z.boolean().optional().describe("Enable exact symbol lookup stage (default: true)"),
      enableVector: z.boolean().optional().describe("Enable vector similarity stage (default: true)"),
      enableLexical: z.boolean().optional().describe("Enable lexical search stage (default: true)"),
      minVectorScore: z.number().optional().describe("Minimum cosine similarity threshold (0–1)"),
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
      const res = await client.api.queryCreate({
        query: params.query,
        workspace: params.workspace,
        workspaces: params.workspaces,
        allWorkspaces: params.allWorkspaces,
        language: params.language,
        kind: params.kind,
        filePath: params.filePath,
        topK: params.topK,
        expandNeighbors: params.expandNeighbors,
        hydrateEdges: params.hydrateEdges,
        retrievalText: params.retrievalText,
        enableSymbolMatch: params.enableSymbolMatch,
        enableVector: params.enableVector,
        enableLexical: params.enableLexical,
        minVectorScore: params.minVectorScore,
      });
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    } catch (error) {
      return {
        isError: true,
        content: [{
          type: "text",
          text: `Error querying codebase: ${error instanceof Error ? error.message : String(error)}`,
        }],
      };
    }
  }
);

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`CodeRag MCP server running (CODERAG_URL=${baseURL})`);
