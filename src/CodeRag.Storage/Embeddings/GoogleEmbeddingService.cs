using CodeRag.Core.Interfaces;
using GenerativeAI;
using GenerativeAI.Types;

namespace CodeRag.Storage.Embeddings;

/// <summary>
/// Embedding service using the Mscc.GenerativeAI SDK (Gemini).
/// </summary>
/// <remarks>
/// Model dimensions:
/// <list type="bullet">
///   <item><term>gemini-embedding-exp-03-07 (GoogleAIModels.GeminiEmbedding)</term><description>3072 dimensions</description></item>
///   <item><term>text-embedding-004</term><description>768 dimensions</description></item>
///   <item><term>text-multilingual-embedding-002</term><description>768 dimensions</description></item>
/// </list>
/// </remarks>
public class GoogleEmbeddingService : IEmbeddingService
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _dimensions;

    public int Dimensions => _dimensions;

    /// <param name="apiKey">Gemini API key from Google AI Studio.</param>
    /// <param name="model">
    /// Model name. Use <c>GoogleAIModels.GeminiEmbedding</c> for the best quality (3072 dims),
    /// or <c>"text-embedding-004"</c> for a lighter model (768 dims).
    /// </param>
    /// <param name="dimensions">Must match the chosen model's output size.</param>
    public GoogleEmbeddingService(string apiKey, string model = GoogleAIModels.GeminiEmbedding, int dimensions = 3072)
    {
        _apiKey = apiKey;
        _model = model;
        _dimensions = dimensions;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var googleAI = new GoogleAi(_apiKey);
        var embeddingModel = googleAI.CreateEmbeddingModel(_model);
        var response = await embeddingModel.EmbedContentAsync(text, cancellationToken: ct);
        return [.. response.Embedding?.Values
            ?? throw new InvalidOperationException("Empty embedding response from Gemini.")];
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var googleAI = new GoogleAi(_apiKey);
        var embeddingModel = googleAI.CreateEmbeddingModel(_model);
        var contents = texts.Select(t => {
            var content = new Content();
            content.AddText(t);
            return content;
        });
        var results = await embeddingModel.BatchEmbedContentAsync(contents, ct);
        return (results.Embeddings ?? throw new InvalidOperationException("Empty batch embedding response from Gemini."))
            .Select(e => (e.Values ?? []).ToArray())
            .ToList();
    }
}

