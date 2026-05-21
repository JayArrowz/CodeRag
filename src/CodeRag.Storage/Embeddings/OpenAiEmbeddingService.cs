using CodeRag.Core.Interfaces;
using OpenAI;
using OpenAI.Embeddings;

namespace CodeRag.Storage.Embeddings;

/// <summary>
/// Embedding service backed by the official OpenAI .NET SDK.
/// Works with OpenAI and any compatible endpoint (Azure OpenAI, local proxies).
/// </summary>
public class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;
    private readonly EmbeddingGenerationOptions _opts;
    private readonly int _dimensions;

    public int Dimensions => _dimensions;

    public OpenAiEmbeddingService(string apiKey, string model = "text-embedding-3-small",
        int dimensions = 1536, string? baseUrl = null)
    {
        _dimensions = dimensions;

        OpenAIClientOptions? clientOptions = null;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            clientOptions = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        }

        var credential = new System.ClientModel.ApiKeyCredential(apiKey);
        var openAiClient = clientOptions is null
            ? new OpenAIClient(credential)
            : new OpenAIClient(credential, clientOptions);

        _client = openAiClient.GetEmbeddingClient(model);
        _opts = new EmbeddingGenerationOptions { Dimensions = dimensions };
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await _client.GenerateEmbeddingAsync(text, _opts, ct);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = await _client.GenerateEmbeddingsAsync(texts, _opts, ct);
        return result.Value
            .OrderBy(e => e.Index)
            .Select(e => e.ToFloats().ToArray())
            .ToList();
    }
}
