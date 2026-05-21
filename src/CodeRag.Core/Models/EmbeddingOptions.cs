namespace CodeRag.Core.Models;

public enum EmbeddingProviderType
{
    OpenAI,
    Google,
}

/// <summary>
/// Configuration block bound from the <c>Embedding</c> section of appsettings.
/// </summary>
/// <example>
/// appsettings.json:
/// <code>
/// "Embedding": {
///   "Provider": "OpenAI",
///   "ApiKey": "sk-...",
///   "Model": "text-embedding-3-small",
///   "Dimensions": 1536
/// }
/// </code>
/// For Google (Gemini):
/// <code>
/// "Embedding": {
///   "Provider": "Google",
///   "ApiKey": "AIza...",
///   "Model": "text-embedding-004",
///   "Dimensions": 768
/// }
/// </code>
/// </example>
public class EmbeddingOptions
{
    /// <summary>Which provider to use. Defaults to OpenAI.</summary>
    public EmbeddingProviderType Provider { get; set; } = EmbeddingProviderType.OpenAI;

    /// <summary>API key for the selected provider.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Model identifier.
    /// OpenAI default: <c>text-embedding-3-small</c> (1536 dims).
    /// Google default: <c>text-embedding-004</c> (768 dims).
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Output vector dimensions. Must match what the chosen model produces.
    /// Leave 0 to use the provider default (1536 for OpenAI, 768 for Google).
    /// </summary>
    public int Dimensions { get; set; } = 0;

    /// <summary>
    /// Optional: override the API base URL (e.g. Azure OpenAI endpoint or a local proxy).
    /// Only applicable for the OpenAI provider.
    /// </summary>
    public string? BaseUrl { get; set; }
}
