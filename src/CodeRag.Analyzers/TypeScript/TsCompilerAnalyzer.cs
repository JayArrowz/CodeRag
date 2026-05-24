using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;

namespace CodeRag.Analyzers.TypeScript;

/// <summary>
/// TypeScript / TSX analyzer backed by the TypeScript Compiler API, executed in a
/// Node.js sidecar process (see <c>tools/ts-analyzer/analyze.js</c>).
///
/// Each project descriptor (typically a <c>tsconfig.json</c>) is mapped to a
/// long-lived sidecar process (cached statically). That process is kept warm
/// across runs so the file-watcher's incremental <see cref="AnalyzeFilesInSolutionAsync"/>
/// calls pay only the per-file refresh cost — not the multi-second
/// tsconfig + program load cost — every time a TypeScript file is saved. This
/// mirrors the warm <c>MSBuildWorkspace</c> cache used by
/// <see cref="CodeRag.Analyzers.CSharp.RoslynAnalyzer"/>.
///
/// The sidecar emits newline-delimited JSON; this class translates the
/// <c>nodeId</c> strings into stable GUIDs (deterministic hash of
/// <c>workspace|nodeId</c>) so re-analysis is idempotent and edges across
/// incremental runs continue to reference the same target chunks even when
/// only a subset of files is re-emitted.
/// </summary>
public partial class TsCompilerAnalyzer : ISolutionAnalyzer
{
    public string[] SupportedExtensions => [".ts", ".tsx"];
    public string LanguageName => "typescript";
    public bool HasSemanticModel => true;

    public Func<string, string, bool>? SupportedSolutionExtensions
    {
        get => (name, ext) => name.StartsWith("tsconfig", StringComparison.InvariantCultureIgnoreCase) && ext.Equals(".json");
    }

    public string[]? ProjectDescriptors => ["tsconfig.json"];

    private static readonly object _installLock = new();
    private static bool _depsChecked;

    private static readonly ConcurrentDictionary<string, string> _sidecarDirCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One <see cref="SidecarSession"/> per project descriptor path, kept warm
    /// so incremental reindexes triggered by the file watcher don't pay the
    /// program-load cost every time a file is saved.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<SidecarSession>>> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-file analysis is intentionally a no-op: the TS compiler needs whole-project
    /// context to resolve symbols. The indexer dispatches .ts/.tsx through
    /// <see cref="AnalyzeSolutionAsync"/> / <see cref="AnalyzeFilesInSolutionAsync"/>.
    /// </summary>
    public Task<AnalysisResult> AnalyzeFileAsync(string filePath, string content,
        string workspace, string? projectName = null)
        => Task.FromResult(new AnalysisResult());

    public async Task<AnalysisResult> AnalyzeSolutionAsync(string solutionOrProjectPath, string workspace)
    {
        var session = await GetOrStartSessionAsync(solutionOrProjectPath);
        try
        {
            var req = JsonSerializer.Serialize(new SidecarRequest { Op = "analyze" }, JsonOpts);
            var lines = await session.SendAsync(req, completionType: "done");
            return BuildResultFromLines(lines, workspace);
        }
        catch
        {
            DropSession(solutionOrProjectPath);
            throw;
        }
    }

    public async Task<AnalysisResult> AnalyzeFilesInSolutionAsync(string solutionOrProjectPath,
        IEnumerable<string> absoluteFilePaths, string workspace)
    {
        var files = absoluteFilePaths.Where(File.Exists).Distinct().ToList();
        if (files.Count == 0) return new AnalysisResult();

        var session = await GetOrStartSessionAsync(solutionOrProjectPath);
        try
        {
            var req = JsonSerializer.Serialize(
                new SidecarRequest { Op = "reanalyze", Files = files }, JsonOpts);
            var lines = await session.SendAsync(req, completionType: "done");
            return BuildResultFromLines(lines, workspace);
        }
        catch
        {
            DropSession(solutionOrProjectPath);
            throw;
        }
    }

    private async Task<SidecarSession> GetOrStartSessionAsync(string projectPath)
    {
        var key = Path.GetFullPath(projectPath);
        var lazy = _sessions.GetOrAdd(key,
            k => new Lazy<Task<SidecarSession>>(() => StartSessionAsync(k)));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            // The cached task faulted — clear the slot so the next call retries.
            _sessions.TryRemove(key, out _);
            throw;
        }
    }

    private static async Task<SidecarSession> StartSessionAsync(string projectPath)
    {
        var sidecarDir = ResolveSidecarDir();
        EnsureDependencies(sidecarDir);

        var nodeExe = ResolveNodeExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            WorkingDirectory = sidecarDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("analyze.js");
        psi.ArgumentList.Add("--server");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start node sidecar at '{nodeExe}'.");

        var session = new SidecarSession(proc, projectPath);
        try
        {
            var openReq = JsonSerializer.Serialize(
                new SidecarRequest { Op = "open", Project = projectPath }, JsonOpts);
            await session.SendAsync(openReq, completionType: "opened");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        Console.WriteLine($"ts-sidecar ready: {projectPath} (pid {proc.Id})");
        return session;
    }

    private static void DropSession(string projectPath)
    {
        var key = Path.GetFullPath(projectPath);
        if (_sessions.TryRemove(key, out var lazy) && lazy.IsValueCreated)
        {
            _ = Task.Run(async () =>
            {
                try { await (await lazy.Value).DisposeAsync(); } catch { /* best-effort */ }
            });
        }
    }

    /// <summary>
    /// Shut down and evict the cached sidecar session for the given project descriptor.
    /// Called when a workspace is removed so the long-lived node process doesn't
    /// outlive its workspace. No-op if no session was started for that path.
    /// </summary>
    public static void EvictSession(string projectPath) => DropSession(projectPath);

    /// <summary>
    /// Shut down and evict every cached sidecar session. Useful at host shutdown
    /// or when bulk-clearing state.
    /// </summary>
    public static void EvictAllSessions()
    {
        foreach (var key in _sessions.Keys.ToArray())
            DropSession(key);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AnalysisResult BuildResultFromLines(IReadOnlyList<string> lines, string workspace)
    {
        var result = new AnalysisResult();
        var nodeIdToGuid = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var chunksByNodeId = new Dictionary<string, CodeChunk>(StringComparer.Ordinal);
        var pendingEdges = new List<SidecarEdge>();

        foreach (var line in lines)
        {
            SidecarEnvelope? env;
            try { env = JsonSerializer.Deserialize<SidecarEnvelope>(line, JsonOpts); }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"ts-sidecar: invalid JSON line ignored: {ex.Message}");
                continue;
            }
            if (env is null) continue;

            switch (env.Type)
            {
                case "chunk":
                    var c = JsonSerializer.Deserialize<SidecarChunk>(line, JsonOpts);
                    if (c is null || string.IsNullOrEmpty(c.NodeId)) break;
                    var id = DeterministicGuid(workspace + "|" + c.NodeId);
                    nodeIdToGuid[c.NodeId] = id;
                    var chunk = ToChunk(c, id);
                    chunksByNodeId[c.NodeId] = chunk;
                    result.Chunks.Add(chunk);
                    break;

                case "edge":
                    var e = JsonSerializer.Deserialize<SidecarEdge>(line, JsonOpts);
                    if (e is null || string.IsNullOrEmpty(e.SourceNodeId)) break;
                    pendingEdges.Add(e);
                    break;

                case "error":
                    Console.Error.WriteLine($"ts-sidecar error: {env.Message} {env.Detail}");
                    break;
            }
        }

        foreach (var e in pendingEdges)
        {
            if (!nodeIdToGuid.TryGetValue(e.SourceNodeId!, out var srcId))
                continue;

            // Even when the target chunk isn't in this batch (incremental case),
            // its GUID is the deterministic hash of workspace|nodeId — same one
            // the original full reindex used — so the cross-file link still
            // matches the chunk already in the store.
            Guid? targetId = null;
            if (!string.IsNullOrEmpty(e.TargetNodeId))
                targetId = DeterministicGuid(workspace + "|" + e.TargetNodeId);

            var srcSig = chunksByNodeId.TryGetValue(e.SourceNodeId!, out var srcChunk)
                ? (srcChunk.Signature ?? srcChunk.FunctionName)
                : string.Empty;

            result.Edges.Add(new CodeEdge
            {
                SourceChunkId = srcId,
                SourceSignature = srcSig,
                TargetChunkId = targetId,
                TargetSignature = e.TargetSignature ?? string.Empty,
                TargetMemberName = e.TargetName,
                TargetDocumentation = e.TargetDocumentation,
                TargetAssembly = e.TargetAssembly,
                TargetNamespace = e.TargetNamespace,
                TargetClassName = e.TargetClassName,
                EdgeKind = e.EdgeKind ?? "calls",
                IsExternal = e.IsExternal,
                FilePath = e.FilePath ?? string.Empty,
                LineNumber = e.LineNumber,
                Language = "typescript",
            });
        }

        return result;
    }

    private static CodeChunk ToChunk(SidecarChunk c, Guid id) => new()
    {
        Id = id,
        Kind = c.Kind ?? "function_declaration",
        Language = "typescript",
        Namespace = c.Namespace,
        ClassName = c.ClassName,
        FunctionName = c.FunctionName ?? c.Name ?? string.Empty,
        Signature = c.Signature,
        FilePath = c.FilePath ?? string.Empty,
        LineNumber = c.StartLine,
        EndLineNumber = c.EndLine,
        Documentation = c.Documentation,
        Body = c.Body,
        ReturnType = c.ReturnType,
        Parameters = c.Parameters ?? new(),
        BaseTypes = c.BaseTypes ?? new(),
        Interfaces = c.Interfaces ?? new(),
        Modifiers = c.Modifiers ?? new(),
    };

    private static string ResolveSidecarDir()
    {
        var envOverride = Environment.GetEnvironmentVariable("CODERAG_TS_ANALYZER_DIR");
        if (!string.IsNullOrEmpty(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        if (_sidecarDirCache.TryGetValue(AppContext.BaseDirectory, out var cached))
            return cached;

        // Collect every candidate that contains analyze.js: the publish/copy
        // location next to the binaries, then every parent dir up to the repo
        // root for dev runs. Prefer one whose node_modules is already populated
        // (typically the source tools/ts-analyzer the developer ran npm in) so
        // we don't pointlessly re-install into the bin output.
        var candidates = new List<string>();
        var copyDir = Path.Combine(AppContext.BaseDirectory, "tools", "ts-analyzer");
        if (File.Exists(Path.Combine(copyDir, "analyze.js"))) candidates.Add(copyDir);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var c = Path.Combine(current.FullName, "tools", "ts-analyzer");
            if (File.Exists(Path.Combine(c, "analyze.js")) &&
                !candidates.Contains(c, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(c);
            }
            current = current.Parent;
        }

        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                "Could not locate tools/ts-analyzer/analyze.js. " +
                "Set CODERAG_TS_ANALYZER_DIR to its absolute path, or ensure the tools/ folder " +
                "is alongside (or above) the running binary.");
        }

        // First: any candidate already populated.
        var ready = candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "node_modules")));
        var chosen = ready ?? candidates[0];
        _sidecarDirCache[AppContext.BaseDirectory] = chosen;
        return chosen;
    }

    private static string ResolveNodeExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODERAG_NODE_PATH");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;
        return OperatingSystem.IsWindows() ? "node.exe" : "node";
    }

    private static void EnsureDependencies(string sidecarDir)
    {
        if (_depsChecked) return;
        lock (_installLock)
        {
            if (_depsChecked) return;
            var modules = Path.Combine(sidecarDir, "node_modules");
            if (!Directory.Exists(modules))
            {
                Console.WriteLine($"Installing TS analyzer dependencies in {sidecarDir} ...");
                // npm on Windows is a .cmd shim; spawning it directly through
                // Process.Start is unreliable (Win32 can't always locate .cmd
                // via PATHEXT when UseShellExecute=false), so route through
                // cmd /c. On Unix npm is a real executable.
                ProcessStartInfo psi;
                if (OperatingSystem.IsWindows())
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        WorkingDirectory = sidecarDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add("npm");
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "npm",
                        WorkingDirectory = sidecarDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                }
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add("--no-audit");
                psi.ArgumentList.Add("--no-fund");
                psi.ArgumentList.Add("--silent");

                using var p = Process.Start(psi)
                    ?? throw new InvalidOperationException($"Failed to launch 'npm install' in {sidecarDir}.");
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    var err = p.StandardError.ReadToEnd();
                    throw new InvalidOperationException(
                        $"'npm install' failed in {sidecarDir} (exit {p.ExitCode}): {err}");
                }
            }
            _depsChecked = true;
        }
    }

    private static Guid DeterministicGuid(string seed)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash);
    }
}