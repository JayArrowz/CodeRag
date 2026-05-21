using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace CodeRag.Storage.Shared;

[Table("code_chunks")]
public class CodeChunkEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("kind")]
    [MaxLength(50)]
    public string Kind { get; set; } = string.Empty;

    [Column("language")]
    [MaxLength(30)]
    public string Language { get; set; } = string.Empty;

    [Column("namespace")]
    [MaxLength(500)]
    public string? Namespace { get; set; }

    [Column("class_name")]
    [MaxLength(300)]
    public string? ClassName { get; set; }

    [Column("function_name")]
    [MaxLength(300)]
    public string FunctionName { get; set; } = string.Empty;

    [Column("signature")]
    [MaxLength(2000)]
    public string? Signature { get; set; }

    [Column("file_path")]
    [MaxLength(1000)]
    public string FilePath { get; set; } = string.Empty;

    [Column("line_number")]
    public int LineNumber { get; set; }

    [Column("end_line_number")]
    public int EndLineNumber { get; set; }

    [Column("documentation", TypeName = "text")]
    public string? Documentation { get; set; }

    [Column("body", TypeName = "text")]
    public string? Body { get; set; }

    [Column("body_summary", TypeName = "text")]
    public string? BodySummary { get; set; }

    [Column("library_assembly")]
    [MaxLength(300)]
    public string? LibraryAssembly { get; set; }

    [Column("library_package")]
    [MaxLength(300)]
    public string? LibraryPackage { get; set; }

    [Column("workspace")]
    [MaxLength(200)]
    public string Workspace { get; set; } = string.Empty;

    [Column("project_name")]
    [MaxLength(200)]
    public string? ProjectName { get; set; }

    [Column("return_type")]
    [MaxLength(500)]
    public string? ReturnType { get; set; }

    [Column("modifiers", TypeName = "text[]")]
    public List<string> Modifiers { get; set; } = [];

    [Column("parameters", TypeName = "text[]")]
    public List<string> Parameters { get; set; } = [];

    [Column("attributes", TypeName = "text[]")]
    public List<string> Attributes { get; set; } = [];

    [Column("calls", TypeName = "text[]")]
    public List<string> Calls { get; set; } = [];

    [Column("base_types", TypeName = "text[]")]
    public List<string> BaseTypes { get; set; } = [];

    [Column("interfaces", TypeName = "text[]")]
    public List<string> Interfaces { get; set; } = [];

    [Column("embedding")]
    public Vector? Embedding { get; set; }

    [Column("indexed_at")]
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
}
