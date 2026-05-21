namespace CodeRag.Core.Interfaces;

/// <summary>
/// Generates embedding vectors from text. Swap between OpenAI, local ONNX models, etc.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// The dimensionality of the vectors this service produces.
    /// </summary>
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
