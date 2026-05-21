using CodeRag.Core.Interfaces;
using OllamaSharp;

namespace CodeRag.Storage.Embeddings;

public class OllamaEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly string _baseUrl;
    private readonly OllamaApiClient _ollamaClient;
    private bool _disposedValue;

    public int Dimensions => _dimensions;

    public OllamaEmbeddingService(string model, int dimensions, string baseUrl, string? apiKey = null)
    {
        _apiKey = apiKey;
        _model = model;
        _dimensions = dimensions;
        _baseUrl = baseUrl;
        _ollamaClient = new OllamaApiClient(new OllamaApiClient.Configuration
        {
            Model = _model,
            Uri = new Uri(_baseUrl),
        });
        if (!string.IsNullOrEmpty(apiKey))
        {
            _ollamaClient.DefaultRequestHeaders.Add("Authorization", $"Bearer: {apiKey}");
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var res = await _ollamaClient.EmbedAsync(text, ct);
        return res.Embeddings.FirstOrDefault()?.ToArray() ?? Array.Empty<float>();
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var res = await _ollamaClient.EmbedAsync(new OllamaSharp.Models.EmbedRequest
        {
            Input = texts.ToList(),
            Model = _model,
        }, ct);
        return res.Embeddings;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _ollamaClient.Dispose();
            }
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
