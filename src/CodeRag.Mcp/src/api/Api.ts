/* eslint-disable */
/* tslint:disable */
// @ts-nocheck
/*
 * ---------------------------------------------------------------
 * ## THIS FILE WAS GENERATED VIA SWAGGER-TYPESCRIPT-API        ##
 * ##                                                           ##
 * ## AUTHOR: acacode                                           ##
 * ## SOURCE: https://github.com/acacode/swagger-typescript-api ##
 * ---------------------------------------------------------------
 */

export interface AddWatchRequest {
  path?: string | null;
  workspace?: string | null;
  project?: string | null;
  includeSubdirectories?: boolean | null;
  enabled?: boolean | null;
}

export interface IndexDirectoryRequest {
  path?: string | null;
  workspace?: string | null;
  project?: string | null;
}

export interface IndexSolutionRequest {
  path?: string | null;
  workspace?: string | null;
}

export interface QueryRequest {
  query?: string | null;
  workspace?: string | null;
  workspaces?: string[] | null;
  allWorkspaces?: boolean;
  language?: string | null;
  languages?: string[] | null;
  project?: string | null;
  projects?: string[] | null;
  kind?: string | null;
  kinds?: string[] | null;
  filePath?: string | null;
  filePathContains?: string[] | null;
  excludeFilePathContains?: string[] | null;
  /** @format int32 */
  topK?: number | null;
  /** @format int32 */
  candidateMultiplier?: number | null;
  enableSymbolMatch?: boolean | null;
  enableVector?: boolean | null;
  enableLexical?: boolean | null;
  /** @format int32 */
  rrfK?: number | null;
  /** @format int32 */
  symbolMaxHits?: number | null;
  /** @format double */
  minVectorScore?: number | null;
  diversifyResults?: boolean | null;
  /** @format int32 */
  maxPerFile?: number | null;
  /** @format int32 */
  maxPerClass?: number | null;
  expandNeighbors?: boolean | null;
  includeContainingType?: boolean | null;
  includeIncomingEdges?: boolean | null;
  /** @format int32 */
  maxIncomingEdges?: number | null;
  hydrateEdges?: boolean | null;
  /** @format int32 */
  tokenBudgetPerResult?: number | null;
  embeddingQueryOverride?: string | null;
  retrievalText?: boolean | null;
  dedupeLibraryDocs?: boolean | null;
}

export interface UpdateWatchRequest {
  enabled?: boolean | null;
}

import type {
  AxiosInstance,
  AxiosRequestConfig,
  AxiosResponse,
  HeadersDefaults,
  ResponseType,
} from "axios";
import axios from "axios";

export type QueryParamsType = Record<string | number, any>;

export interface FullRequestParams
  extends Omit<AxiosRequestConfig, "data" | "params" | "url" | "responseType"> {
  /** set parameter to `true` for call `securityWorker` for this request */
  secure?: boolean;
  /** request path */
  path: string;
  /** content type of request body */
  type?: ContentType;
  /** query params */
  query?: QueryParamsType;
  /** format of response (i.e. response.json() -> format: "json") */
  format?: ResponseType;
  /** request body */
  body?: unknown;
}

export type RequestParams = Omit<
  FullRequestParams,
  "body" | "method" | "query" | "path"
>;

export interface ApiConfig<SecurityDataType = unknown>
  extends Omit<AxiosRequestConfig, "data" | "cancelToken"> {
  securityWorker?: (
    securityData: SecurityDataType | null,
  ) => Promise<AxiosRequestConfig | void> | AxiosRequestConfig | void;
  secure?: boolean;
  format?: ResponseType;
}

export enum ContentType {
  Json = "application/json",
  JsonApi = "application/vnd.api+json",
  FormData = "multipart/form-data",
  UrlEncoded = "application/x-www-form-urlencoded",
  Text = "text/plain",
}

export class HttpClient<SecurityDataType = unknown> {
  public instance: AxiosInstance;
  private securityData: SecurityDataType | null = null;
  private securityWorker?: ApiConfig<SecurityDataType>["securityWorker"];
  private secure?: boolean;
  private format?: ResponseType;

  constructor({
    securityWorker,
    secure,
    format,
    ...axiosConfig
  }: ApiConfig<SecurityDataType> = {}) {
    this.instance = axios.create({
      ...axiosConfig,
      baseURL: axiosConfig.baseURL || "",
    });
    this.secure = secure;
    this.format = format;
    this.securityWorker = securityWorker;
  }

  public setSecurityData = (data: SecurityDataType | null) => {
    this.securityData = data;
  };

  protected mergeRequestParams(
    params1: AxiosRequestConfig,
    params2?: AxiosRequestConfig,
  ): AxiosRequestConfig {
    const method = params1.method || (params2 && params2.method);

    return {
      ...this.instance.defaults,
      ...params1,
      ...(params2 || {}),
      headers: {
        ...((method &&
          this.instance.defaults.headers[
            method.toLowerCase() as keyof HeadersDefaults
          ]) ||
          {}),
        ...(params1.headers || {}),
        ...((params2 && params2.headers) || {}),
      },
    };
  }

  protected stringifyFormItem(formItem: unknown) {
    if (typeof formItem === "object" && formItem !== null) {
      return JSON.stringify(formItem);
    } else {
      return `${formItem}`;
    }
  }

  protected createFormData(input: Record<string, unknown>): FormData {
    if (input instanceof FormData) {
      return input;
    }
    return Object.keys(input || {}).reduce((formData, key) => {
      const property = input[key];
      const propertyContent: any[] =
        property instanceof Array ? property : [property];

      for (const formItem of propertyContent) {
        const isFileType = formItem instanceof Blob || formItem instanceof File;
        formData.append(
          key,
          isFileType ? formItem : this.stringifyFormItem(formItem),
        );
      }

      return formData;
    }, new FormData());
  }

  public request = async <T = any, _E = any>({
    secure,
    path,
    type,
    query,
    format,
    body,
    ...params
  }: FullRequestParams): Promise<AxiosResponse<T>> => {
    const secureParams =
      ((typeof secure === "boolean" ? secure : this.secure) &&
        this.securityWorker &&
        (await this.securityWorker(this.securityData))) ||
      {};
    const requestParams = this.mergeRequestParams(params, secureParams);
    const responseFormat = format || this.format || undefined;

    if (
      type === ContentType.FormData &&
      body &&
      body !== null &&
      typeof body === "object"
    ) {
      body = this.createFormData(body as Record<string, unknown>);
    }

    if (
      type === ContentType.Text &&
      body &&
      body !== null &&
      typeof body !== "string"
    ) {
      body = JSON.stringify(body);
    }

    return this.instance.request({
      ...requestParams,
      headers: {
        ...(requestParams.headers || {}),
        ...(type ? { "Content-Type": type } : {}),
      },
      params: query,
      responseType: responseFormat,
      data: body,
      url: path,
    });
  };
}

/**
 * @title CodeRag API
 * @version v1
 *
 * HTTP surface for indexing, semantic search, and graph queries.
 */
export class Api<
  SecurityDataType extends unknown,
> extends HttpClient<SecurityDataType> {
  api = {
    /**
     * No description
     *
     * @tags CodeRag
     * @name InitCreate
     * @summary Create the database schema. Idempotent.
     * @request POST:/api/init
     */
    initCreate: (params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/init`,
        method: "POST",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name IndexSolutionCreate
     * @summary Start indexing a .sln / .csproj. Returns a job id.
     * @request POST:/api/index/solution
     */
    indexSolutionCreate: (
      data: IndexSolutionRequest,
      params: RequestParams = {},
    ) =>
      this.request<void, any>({
        path: `/api/index/solution`,
        method: "POST",
        body: data,
        type: ContentType.Json,
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name IndexDirectoryCreate
     * @summary Start indexing a source directory. Returns a job id.
     * @request POST:/api/index/directory
     */
    indexDirectoryCreate: (
      data: IndexDirectoryRequest,
      params: RequestParams = {},
    ) =>
      this.request<void, any>({
        path: `/api/index/directory`,
        method: "POST",
        body: data,
        type: ContentType.Json,
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name QueryCreate
     * @summary Hybrid AI-context search: vector + lexical + symbol fast-path, fused with RRF, plus neighborhood expansion and outgoing-edge hydration. Returns LLM-ready text blocks when retrievalText=true.
     * @request POST:/api/query
     */
    queryCreate: (data: QueryRequest, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/query`,
        method: "POST",
        body: data,
        type: ContentType.Json,
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name StatsList
     * @summary Global store statistics.
     * @request GET:/api/stats
     */
    statsList: (params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/stats`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WorkspacesList
     * @summary List indexed workspaces with chunk/edge counts.
     * @request GET:/api/workspaces
     */
    workspacesList: (params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/workspaces`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WorkspacesDelete
     * @summary Drop all chunks, edges, and watchers for a workspace.
     * @request DELETE:/api/workspaces/{name}
     */
    workspacesDelete: (name: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/workspaces/${name}`,
        method: "DELETE",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name ProjectsDelete
     * @summary Drop all chunks and edges for a project.
     * @request DELETE:/api/projects/{name}
     */
    projectsDelete: (name: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/projects/${name}`,
        method: "DELETE",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name FilesDelete
     * @summary Drop all chunks and edges for a file path.
     * @request DELETE:/api/files
     */
    filesDelete: (
      query: {
        path: string;
      },
      params: RequestParams = {},
    ) =>
      this.request<void, any>({
        path: `/api/files`,
        method: "DELETE",
        query: query,
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name ChunksEdgesOutgoingList
     * @summary Outgoing edges (calls / creates / inherits / implements) from a chunk.
     * @request GET:/api/chunks/{id}/edges/outgoing
     */
    chunksEdgesOutgoingList: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/chunks/${id}/edges/outgoing`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name ChunksEdgesIncomingList
     * @summary Incoming edges pointing at a chunk.
     * @request GET:/api/chunks/{id}/edges/incoming
     */
    chunksEdgesIncomingList: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/chunks/${id}/edges/incoming`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name JobsList
     * @summary All indexing jobs (queued / running / finished).
     * @request GET:/api/jobs
     */
    jobsList: (params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/jobs`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name JobsDetail
     * @summary Single job with full log output.
     * @request GET:/api/jobs/{id}
     */
    jobsDetail: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/jobs/${id}`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name JobsDelete
     * @summary Remove a finished job from the registry.
     * @request DELETE:/api/jobs/{id}
     */
    jobsDelete: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/jobs/${id}`,
        method: "DELETE",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name JobsCancelCreate
     * @summary Cancel a running or queued job.
     * @request POST:/api/jobs/{id}/cancel
     */
    jobsCancelCreate: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/jobs/${id}/cancel`,
        method: "POST",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WatchesList
     * @summary List configured directory watches (auto-reindex roots).
     * @request GET:/api/watches
     */
    watchesList: (params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/watches`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WatchesCreate
     * @summary Register a directory to be auto-synced to the index.
     * @request POST:/api/watches
     */
    watchesCreate: (data: AddWatchRequest, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/watches`,
        method: "POST",
        body: data,
        type: ContentType.Json,
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WatchesEventsList
     * @summary Recent watcher events (reindex / remove / sweep / error).
     * @request GET:/api/watches/events
     */
    watchesEventsList: (params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/watches/events`,
        method: "GET",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WatchesPartialUpdate
     * @summary Enable or disable a watch.
     * @request PATCH:/api/watches/{id}
     */
    watchesPartialUpdate: (
      id: string,
      data: UpdateWatchRequest,
      params: RequestParams = {},
    ) =>
      this.request<void, any>({
        path: `/api/watches/${id}`,
        method: "PATCH",
        body: data,
        type: ContentType.Json,
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WatchesDelete
     * @summary Stop watching this root (does not delete indexed data).
     * @request DELETE:/api/watches/{id}
     */
    watchesDelete: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/watches/${id}`,
        method: "DELETE",
        ...params,
      }),

    /**
     * No description
     *
     * @tags CodeRag
     * @name WatchesSweepCreate
     * @summary Force a catch-up sweep for this watch right now.
     * @request POST:/api/watches/{id}/sweep
     */
    watchesSweepCreate: (id: string, params: RequestParams = {}) =>
      this.request<void, any>({
        path: `/api/watches/${id}/sweep`,
        method: "POST",
        ...params,
      }),
  };
}
