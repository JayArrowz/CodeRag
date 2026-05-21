using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Storage.Embeddings;

public static class EmbeddingServiceFactory
{
    /// <summary>
    /// Registers the <see cref="IEmbeddingService"/> matching the <c>Embedding</c> config section.
    /// Falls back to <see cref="FakeEmbeddingService"/> when no API key is configured.
    /// </summary>
    public static IServiceCollection AddEmbeddingService(
        this IServiceCollection services, IConfiguration config)
    {
        var opts = config.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();

        if (opts.Dimensions == 0 && int.TryParse(config["EmbeddingDimensions"], out var d))
            opts.Dimensions = d;

        var service = Build(opts);
        services.AddSingleton(service);
        return services;
    }

    /// <summary>Build an <see cref="IEmbeddingService"/> from an <see cref="EmbeddingOptions"/> instance.</summary>
    public static IEmbeddingService Build(EmbeddingOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            var dims = EffectiveDimensions(opts);
            return new FakeEmbeddingService(dims);
        }

        return opts.Provider switch
        {
            EmbeddingProviderType.OpenAI => BuildOpenAi(opts),
            EmbeddingProviderType.Google => BuildGoogle(opts),
            EmbeddingProviderType.Ollama => BuildOllama(opts),
            _ => throw new InvalidOperationException($"Unknown embedding provider: {opts.Provider}"),
        };
    }

    private static IEmbeddingService BuildOllama(EmbeddingOptions opts)
    {
        var model = string.IsNullOrWhiteSpace(opts.Model) ? "text-embedding-3-small" : opts.Model;
        var dims = opts.Dimensions > 0 ? opts.Dimensions : 1536;
        return new OllamaEmbeddingService(model, dims, opts.BaseUrl ?? "http://localhost:11434", opts.ApiKey);
    }

    private static IEmbeddingService BuildOpenAi(EmbeddingOptions opts)
    {
        var model = string.IsNullOrWhiteSpace(opts.Model) ? "text-embedding-3-small" : opts.Model;
        var dims = opts.Dimensions > 0 ? opts.Dimensions : 1536;
        return new OpenAiEmbeddingService(opts.ApiKey, model, dims, opts.BaseUrl);
    }

    private static IEmbeddingService BuildGoogle(EmbeddingOptions opts)
    {
        var model = string.IsNullOrWhiteSpace(opts.Model) ? GenerativeAI.GoogleAIModels.GeminiEmbedding : opts.Model;
        var dims = opts.Dimensions > 0 ? opts.Dimensions : 3072;
        return new GoogleEmbeddingService(opts.ApiKey, model, dims);
    }

    private static int EffectiveDimensions(EmbeddingOptions opts) => opts.Dimensions > 0
        ? opts.Dimensions
        : opts.Provider == EmbeddingProviderType.Google ? 3072 : 1536;
}
