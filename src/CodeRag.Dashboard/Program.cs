using CodeRag.Analyzers.CSharp;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Services;
using CodeRag.Dashboard.Api;
using CodeRag.Dashboard.Components;
using CodeRag.Dashboard.Services;
using CodeRag.Storage.Embeddings;
using CodeRag.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

// CODERAG_ prefixed env vars override appsettings.json keys
builder.Configuration.AddEnvironmentVariables("CODERAG_");

var config = builder.Configuration;

// CodeRag pipeline
builder.Services.AddPgVectorStore(config);
builder.Services.AddEmbeddingService(config);

builder.Services.AddSingleton<ILanguageAnalyzer, RoslynAnalyzer>();
builder.Services.AddSingleton<CodebaseIndexer>();
builder.Services.AddSingleton<IndexingJobService>();
builder.Services.AddSingleton<WatchPersistence>();
builder.Services.AddSingleton<FileWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileWatcherService>());
builder.Services.AddScoped<CodeExplorerService>();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// HTTP API (mirrors the CLI; future MCP server target).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "CodeRag API",
        Version = "v1",
        Description = "HTTP surface for indexing, semantic search, and graph queries."
    });
});

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

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeRag API v1");
    c.RoutePrefix = "swagger";
});

app.MapCodeRagApi();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
