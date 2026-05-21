using CodeRag.Storage.Postgres;
using CodeRag.Storage.Postgres.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeRag.Dashboard.Services;

/// <summary>
/// Query service for the interactive code explorer — lazy-loads the
/// project → namespace → class → member hierarchy and edges.
/// </summary>
public class CodeExplorerService(IDbContextFactory<CodeRagDbContext> dbFactory)
{
    internal const string NoProject = "(no project)";
    internal const string GlobalNs  = "(global namespace)";

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
        return await db.CodeEdges
            .Where(e => e.SourceChunkId == chunkId)
            .OrderBy(e => e.LineNumber)
            .ToListAsync();
    }

    public async Task<List<CodeEdgeEntity>> GetIncomingEdgesAsync(Guid chunkId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CodeEdges
            .Where(e => e.TargetChunkId == chunkId)
            .OrderBy(e => e.SourceSignature)
            .ToListAsync();
    }
}
