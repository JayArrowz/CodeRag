using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CodeRag.Storage.Postgres.Entities;

[Table("code_edges")]
public class CodeEdgeEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("source_chunk_id")]
    public Guid SourceChunkId { get; set; }

    [Column("source_signature")]
    [MaxLength(2000)]
    public string SourceSignature { get; set; } = string.Empty;

    [Column("target_chunk_id")]
    public Guid? TargetChunkId { get; set; }

    [Column("target_signature")]
    [MaxLength(2000)]
    public string TargetSignature { get; set; } = string.Empty;

    [Column("target_namespace")]
    [MaxLength(500)]
    public string? TargetNamespace { get; set; }

    [Column("target_class_name")]
    [MaxLength(300)]
    public string? TargetClassName { get; set; }

    [Column("target_member_name")]
    [MaxLength(300)]
    public string? TargetMemberName { get; set; }

    [Column("target_assembly")]
    [MaxLength(300)]
    public string? TargetAssembly { get; set; }

    [Column("target_documentation")]
    public string? TargetDocumentation { get; set; }

    [Column("edge_kind")]
    [MaxLength(30)]
    public string EdgeKind { get; set; } = "calls";

    [Column("is_external")]
    public bool IsExternal { get; set; }

    [Column("file_path")]
    [MaxLength(1000)]
    public string FilePath { get; set; } = string.Empty;

    [Column("line_number")]
    public int LineNumber { get; set; }

    [Column("workspace")]
    [MaxLength(200)]
    public string Workspace { get; set; } = string.Empty;

    [Column("project_name")]
    [MaxLength(200)]
    public string? ProjectName { get; set; }

    [Column("language")]
    [MaxLength(30)]
    public string Language { get; set; } = string.Empty;
}
