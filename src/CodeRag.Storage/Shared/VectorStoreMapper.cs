using CodeRag.Core.Models;
using Pgvector;
using static CodeRag.Storage.Shared.VectorStoreHelper;

namespace CodeRag.Storage.Shared;

internal static class VectorStoreMapper
{
    internal static CodeChunkEntity ToEntity(CodeChunk chunk) => new()
    {
        Id = chunk.Id,
        Kind = chunk.Kind,
        Language = chunk.Language,
        Namespace = chunk.Namespace,
        ClassName = chunk.ClassName,
        FunctionName = chunk.FunctionName,
        Signature = chunk.Signature,
        FilePath = chunk.FilePath,
        LineNumber = chunk.LineNumber,
        EndLineNumber = chunk.EndLineNumber,
        Documentation = chunk.Documentation,
        Body = chunk.Body,
        BodySummary = chunk.BodySummary,
        LibraryAssembly = chunk.LibraryAssembly,
        LibraryPackage = chunk.LibraryPackage,
        Workspace = chunk.Workspace,
        ProjectName = chunk.ProjectName,
        ReturnType = chunk.ReturnType,
        Modifiers = chunk.Modifiers,
        Parameters = chunk.Parameters,
        Attributes = chunk.Attributes,
        Calls = chunk.Calls,
        BaseTypes = chunk.BaseTypes,
        Interfaces = chunk.Interfaces,
        Embedding = chunk.Embedding is not null ? new Vector(chunk.Embedding) : null,
        IndexedAt = chunk.IndexedAt,
    };

    internal static CodeChunk FromEntity(CodeChunkEntity entity) => new()
    {
        Id = entity.Id,
        Kind = entity.Kind,
        Language = entity.Language,
        Namespace = entity.Namespace,
        ClassName = entity.ClassName,
        FunctionName = entity.FunctionName,
        Signature = entity.Signature,
        FilePath = entity.FilePath,
        LineNumber = entity.LineNumber,
        EndLineNumber = entity.EndLineNumber,
        Documentation = entity.Documentation,
        Body = entity.Body,
        BodySummary = entity.BodySummary,
        LibraryAssembly = entity.LibraryAssembly,
        LibraryPackage = entity.LibraryPackage,
        Workspace = entity.Workspace,
        ProjectName = entity.ProjectName,
        ReturnType = entity.ReturnType,
        Modifiers = entity.Modifiers,
        Parameters = entity.Parameters,
        Attributes = entity.Attributes,
        Calls = entity.Calls,
        BaseTypes = entity.BaseTypes,
        Interfaces = entity.Interfaces,
        Embedding = entity.Embedding?.ToArray(),
        IndexedAt = entity.IndexedAt,
    };

    internal static CodeEdgeEntity ToEdgeEntity(CodeEdge edge) => new()
    {
        Id = edge.Id,
        SourceChunkId = edge.SourceChunkId,
        SourceSignature = Truncate(edge.SourceSignature, 2000),
        TargetChunkId = edge.TargetChunkId,
        TargetSignature = Truncate(edge.TargetSignature, 2000),
        TargetNamespace = TruncateOpt(edge.TargetNamespace, 500),
        TargetClassName = TruncateOpt(edge.TargetClassName, 300),
        TargetMemberName = TruncateOpt(edge.TargetMemberName, 300),
        TargetAssembly = TruncateOpt(edge.TargetAssembly, 300),
        TargetDocumentation = edge.TargetDocumentation,
        EdgeKind = edge.EdgeKind,
        IsExternal = edge.IsExternal,
        FilePath = edge.FilePath,
        LineNumber = edge.LineNumber,
        Workspace = edge.Workspace,
        ProjectName = edge.ProjectName,
        Language = edge.Language,
    };

    internal static CodeEdge FromEdgeEntity(CodeEdgeEntity e) => new()
    {
        Id = e.Id,
        SourceChunkId = e.SourceChunkId,
        SourceSignature = e.SourceSignature,
        TargetChunkId = e.TargetChunkId,
        TargetSignature = e.TargetSignature,
        TargetNamespace = e.TargetNamespace,
        TargetClassName = e.TargetClassName,
        TargetMemberName = e.TargetMemberName,
        TargetAssembly = e.TargetAssembly,
        TargetDocumentation = e.TargetDocumentation,
        EdgeKind = e.EdgeKind,
        IsExternal = e.IsExternal,
        FilePath = e.FilePath,
        LineNumber = e.LineNumber,
        Workspace = e.Workspace,
        ProjectName = e.ProjectName,
        Language = e.Language,
    };
}
