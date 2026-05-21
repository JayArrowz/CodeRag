using CodeRag.Analyzers.CSharp;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Services;
using CodeRag.Dashboard.Components;
using CodeRag.Dashboard.Services;
using CodeRag.Storage.Embeddings;
using CodeRag.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

// CODERAG_ prefixed env vars override appsettings.json keys
builder.Configuration.AddEnvironmentVariables("CODERAG_");

var config = builder.Configuration;
var openAiKey = config["OpenAiApiKey"] ?? "";
var embeddingModel = config["EmbeddingModel"] ?? "text-embedding-3-small";
var embeddingDimensions = int.TryParse(config["EmbeddingDimensions"], out var d) ? d : 1536;

// CodeRag pipeline
builder.Services.AddPgVectorStore(config);

if (!string.IsNullOrEmpty(openAiKey))
{
    builder.Services.AddSingleton<IEmbeddingService>(
        new OpenAiEmbeddingService(openAiKey, embeddingModel, embeddingDimensions));
}
else
{
    builder.Services.AddSingleton<IEmbeddingService>(new FakeEmbeddingService(embeddingDimensions));
}

builder.Services.AddSingleton<ILanguageAnalyzer, RoslynAnalyzer>();
builder.Services.AddSingleton<CodebaseIndexer>();
builder.Services.AddSingleton<IndexingJobService>();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Hook job-aware console writer so background indexing output is captured per-job.
JobConsoleWriter.Install();

// Ensure DB schema exists on startup (best-effort).
using (var scope = app.Services.CreateScope())
{
    try
    {
        var store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        await store.InitializeAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Vector store initialization failed at startup");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
