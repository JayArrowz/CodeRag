using CodeRag.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace CodeRag.Dashboard.Services;

/// <summary>
/// Query service for the interactive code explorer — lazy-loads the
/// project → namespace → class → member hierarchy and edges.
/// </summary>
public class CodeExplorerService(IDbContextFactory<CodeRagDbContextBase> dbFactory)
{
    internal const string NoProject = "(no project)";
    internal const string GlobalNs = "(global namespace)";

    public async Task<List<string>> GetProjectsAsync(string workspace)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CodeChunks
            .Where(c => c.Workspace == workspace)
            .Select(c => c.ProjectName ?? NoProject)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync();
    }

    public async Task<List<string>> GetNamespacesAsync(string workspace, string project)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.CodeChunks.Where(c => c.Workspace == workspace);
        q = project == NoProject
            ? q.Where(c => c.ProjectName == null)
            : q.Where(c => c.ProjectName == project);
        return await q
            .Select(c => c.Namespace ?? GlobalNs)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync();
    }

    public async Task<List<string>> GetClassesAsync(string workspace, string project, string ns)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.CodeChunks.Where(c => c.Workspace == workspace);
        q = project == NoProject ? q.Where(c => c.ProjectName == null) : q.Where(c => c.ProjectName == project);
        q = ns == GlobalNs ? q.Where(c => c.Namespace == null) : q.Where(c => c.Namespace == ns);
        return await q
            .Where(c => !string.IsNullOrEmpty(c.ClassName))
            .Select(c => c.ClassName!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    public async Task<List<CodeChunkEntity>> GetMembersAsync(
        string workspace, string project, string ns, string className)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.CodeChunks.Where(c => c.Workspace == workspace && c.ClassName == className);
        q = project == NoProject ? q.Where(c => c.ProjectName == null) : q.Where(c => c.ProjectName == project);
        q = ns == GlobalNs ? q.Where(c => c.Namespace == null) : q.Where(c => c.Namespace == ns);
        return await q.OrderBy(c => c.LineNumber).ToListAsync();
    }

    public async Task<CodeChunkEntity?> GetChunkAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CodeChunks.FindAsync(id);
    }

    public async Task<List<CodeEdgeEntity>> GetOutgoingEdgesAsync(Guid chunkId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var edges = await db.CodeEdges
            .Where(e => e.SourceChunkId == chunkId)
            .OrderBy(e => e.LineNumber)
            .ToListAsync();

        await ResolveUnlinkedEdgesAsync(db, edges);
        return edges;
    }

    public async Task<List<CodeEdgeEntity>> GetIncomingEdgesAsync(Guid chunkId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CodeEdges
            .Where(e => e.TargetChunkId == chunkId)
            .OrderBy(e => e.SourceSignature)
            .ToListAsync();
    }

    /// <summary>
    /// For any edge whose <c>TargetChunkId</c> was never resolved at index time (happens when
    /// the callee was indexed in a different run or batch), attempt a signature match against
    /// chunks in the workspace and persist the result so the next query is instant.
    /// </summary>
    private static async Task ResolveUnlinkedEdgesAsync(CodeRagDbContextBase db, List<CodeEdgeEntity> edges)
    {
        var unresolved = edges
            .Where(e => e.TargetChunkId is null && !e.IsExternal && !string.IsNullOrEmpty(e.TargetSignature))
            .ToList();
        if (unresolved.Count == 0) return;

        var signatures = unresolved.Select(e => e.TargetSignature!).Distinct().ToList();

        // One query: fetch all matching chunks by signature.
        var matched = await db.CodeChunks
            .Where(c => signatures.Contains(c.Signature!))
            .Select(c => new { c.Signature, c.Id })
            .ToListAsync();

        if (matched.Count == 0) return;

        // Last-write-wins for duplicate signatures (e.g. overloads with same string form).
        var bySignature = matched
            .GroupBy(m => m.Signature!)
            .ToDictionary(g => g.Key, g => g.First().Id);

        bool anyUpdated = false;
        foreach (var edge in unresolved)
        {
            if (!bySignature.TryGetValue(edge.TargetSignature!, out var id)) continue;
            edge.TargetChunkId = id;
            edge.IsExternal = false;
            anyUpdated = true;
        }

        if (anyUpdated)
            await db.SaveChangesAsync();
    }
}
