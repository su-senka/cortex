using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OllamaSharp;
using Microsoft.Extensions.AI;
using RagAssistant.Core;
using RagAssistant.Core.Ingestion;
using RagAssistant.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));

var ollamaCfg = builder.Configuration.GetSection("Ollama");
var ragCfg = builder.Configuration.GetSection("Rag");

var ollamaBaseUrl = ollamaCfg["BaseUrl"] ?? "http://localhost:11434";
var chatModel = ollamaCfg["ChatModel"] ?? "qwen2.5:7b-instruct-q4_K_M";
var embeddingModel = ollamaCfg["EmbeddingModel"] ?? "nomic-embed-text";
var vectorDbPath = ragCfg["VectorDbPath"] ?? "rag_store.db";

// Resolve VectorDbPath relative to the app's content root so it lands next to the binary,
// not wherever the working directory happens to be (important on IIS).
vectorDbPath = Path.IsPathRooted(vectorDbPath)
    ? vectorDbPath
    : Path.Combine(builder.Environment.ContentRootPath, vectorDbPath);

var vectorDbConnectionString = $"Data Source={vectorDbPath}";

// ── AI clients ────────────────────────────────────────────────────────────────

// OllamaApiClient explicitly implements both IChatClient and
// IEmbeddingGenerator<string, Embedding<float>> from Microsoft.Extensions.AI.
// We create two instances (one per model) and register them under the interfaces.
builder.Services.AddSingleton<IChatClient>(
    _ => (IChatClient)new OllamaApiClient(new Uri(ollamaBaseUrl), chatModel));

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    _ => (IEmbeddingGenerator<string, Embedding<float>>)new OllamaApiClient(
        new Uri(ollamaBaseUrl), embeddingModel));

// ── Vector store ──────────────────────────────────────────────────────────────

// SqliteCollection<TKey, TRecord> extends VectorStoreCollection<TKey, TRecord>.
// Both services inject the abstract base class, keeping Core independent of SqliteVec.
builder.Services.AddSingleton<VectorStoreCollection<string, DocumentChunk>>(
    _ => new SqliteCollection<string, DocumentChunk>(
        vectorDbConnectionString, "doc_chunks", new SqliteCollectionOptions()));

// ── Application services ──────────────────────────────────────────────────────

builder.Services.AddSingleton<MarkdownIngestionService>();
builder.Services.AddSingleton<RagQueryService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Run ingestion at startup (convenient during dev; remove for production) ───

using (var scope = app.Services.CreateScope())
{
    var ingestion = scope.ServiceProvider.GetRequiredService<MarkdownIngestionService>();
    try
    {
        app.Logger.LogInformation("Running startup ingestion...");
        await ingestion.IngestAllAsync();
        app.Logger.LogInformation("Startup ingestion complete.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Startup ingestion failed — app will still start.");
    }
}

// ── API endpoints ─────────────────────────────────────────────────────────────

// POST /api/ingest — rescans the docs folder and upserts changes.
app.MapPost("/api/ingest", async (
    MarkdownIngestionService ingestion,
    CancellationToken ct) =>
{
    await ingestion.IngestAllAsync(ct);
    return Results.Ok(new { message = "Ingestion complete." });
});

// POST /api/chat — streams the answer as Server-Sent Events.
// Request:  { "question": "..." }
// Response: text/event-stream
//   data: {"t":"sources","v":[{"sourceFile":"...","title":"...","sectionHeading":"...","score":0.9},...]}
//   data: {"t":"text","v":"token text"}
//   ...
//   data: [DONE]
app.MapPost("/api/chat", async (
    ChatRequest req,
    RagQueryService rag,
    HttpResponse response,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
    {
        response.StatusCode = 400;
        await response.WriteAsync("question is required", ct);
        return;
    }

    response.ContentType = "text/event-stream; charset=utf-8";
    response.Headers.CacheControl = "no-cache";
    // Disable nginx/IIS response buffering so tokens reach the browser immediately.
    response.Headers["X-Accel-Buffering"] = "no";

    try
    {
        var result = await rag.QueryAsync(req.Question, ct);

        // Send sources immediately — they come from vector search, not the LLM.
        var sourcesJson = JsonSerializer.Serialize(new
        {
            t = "sources",
            v = result.Sources
        }, SseJsonOptions);
        await response.WriteAsync($"data: {sourcesJson}\n\n", ct);
        await response.Body.FlushAsync(ct);

        // Stream LLM tokens.
        await foreach (var token in result.AnswerStream.WithCancellation(ct))
        {
            var tokenJson = JsonSerializer.Serialize(new { t = "text", v = token }, SseJsonOptions);
            await response.WriteAsync($"data: {tokenJson}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    catch (Exception ex)
    {
        var errJson = JsonSerializer.Serialize(new { t = "error", v = ex.Message }, SseJsonOptions);
        await response.WriteAsync($"data: {errJson}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
    finally
    {
        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
});

app.Run();

// ── Supporting types ──────────────────────────────────────────────────────────

record ChatRequest(string Question);

// Camel-case JSON for the SSE payload; consistent with JavaScript conventions.
static partial class Program
{
    internal static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
