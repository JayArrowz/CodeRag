using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRag.Core.Interfaces;

namespace CodeRag.Storage.Embeddings;

/// <summary>
/// Embedding service using the OpenAI-compatible API (works with OpenAI, Azure OpenAI, Ollama, etc.).
/// </summary>
public class OpenAiEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly int _dimensions;

    public int Dimensions => _dimensions;

    public OpenAiEmbeddingService(string apiKey, string model = "text-embedding-3-small",
        int dimensions = 1536, string baseUrl = "https://api.openai.com/v1")
    {
        _model = model;
        _dimensions = dimensions;
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await EmbedBatchAsync([text], ct);
        return result[0];
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var request = new
        {
            model = _model,
            input = texts,
            dimensions = _dimensions,
        };

        var response = await _httpClient.PostAsJsonAsync("embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
            ?? throw new InvalidOperationException("Empty embedding response");

        return body.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
    }

    public void Dispose() => _httpClient.Dispose();

    private record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingData> Data
    );

    private record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding
    );
}

/// <summary>
/// A no-op embedding service for testing. Returns random vectors.
/// </summary>
public class FakeEmbeddingService : IEmbeddingService
{
    public int Dimensions { get; }
    private readonly Random _rng = new(42);

    public FakeEmbeddingService(int dimensions = 1536) => Dimensions = dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vec = new float[Dimensions];
        for (int i = 0; i < Dimensions; i++)
            vec[i] = (float)_rng.NextDouble();
        Normalize(vec);
        return Task.FromResult(vec);
    }

    public Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>();
        foreach (var _ in texts)
            results.Add(EmbedAsync("", ct).Result);
        return Task.FromResult(results);
    }

    private static void Normalize(float[] vec)
    {
        var mag = MathF.Sqrt(vec.Sum(x => x * x));
        if (mag > 0)
            for (int i = 0; i < vec.Length; i++)
                vec[i] /= mag;
    }
}
