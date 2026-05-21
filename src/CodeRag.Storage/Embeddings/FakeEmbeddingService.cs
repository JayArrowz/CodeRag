using CodeRag.Core.Interfaces;

namespace CodeRag.Storage.Embeddings;

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
