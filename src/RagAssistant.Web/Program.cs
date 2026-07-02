using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OllamaSharp;
using RagAssistant.Core;
using RagAssistant.Core.Conversations;
using RagAssistant.Core.Ingestion;
using RagAssistant.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.Configure<AzureDevOpsOptions>(builder.Configuration.GetSection("AzureDevOps"));

var ollamaCfg  = builder.Configuration.GetSection("Ollama");
var ragCfg     = builder.Configuration.GetSection("Rag");
var oidcCfg    = builder.Configuration.GetSection("Oidc");

var requireHttpsMetadata = oidcCfg.GetValue("RequireHttpsMetadata", true);
var validateIssuer       = oidcCfg.GetValue("ValidateIssuer", true);
var validateAudience     = oidcCfg.GetValue("ValidateAudience", true);
var validIssuers         = oidcCfg.GetSection("ValidIssuers").Get<string[]>();
var validAudiences       = oidcCfg.GetSection("ValidAudiences").Get<string[]>();
var adminRole            = oidcCfg["AdminRole"] ?? "cortex-admin";
var cookieSecurePolicy   = requireHttpsMetadata
    ? CookieSecurePolicy.Always
    : CookieSecurePolicy.None;

var ollamaBaseUrl  = ollamaCfg["BaseUrl"]      ?? "http://localhost:11434";
var chatModel      = ollamaCfg["ChatModel"]    ?? "qwen2.5:7b-instruct-q4_K_M";
var embeddingModel = ollamaCfg["EmbeddingModel"] ?? "nomic-embed-text";
var vectorDbPath   = ragCfg["VectorDbPath"]    ?? "rag_store.db";

vectorDbPath = Path.IsPathRooted(vectorDbPath)
    ? vectorDbPath
    : Path.Combine(builder.Environment.ContentRootPath, vectorDbPath);

var vectorDbConnectionString = $"Data Source={vectorDbPath}";

// ── Authentication — Keycloak OIDC ────────────────────────────────────────────

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        // API calls must get 401, not a redirect to login, so SSE and fetch work correctly.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(o =>
    {
        // Authority is the internal Keycloak URL — used for discovery doc fetch and token validation.
        // PublicOrigin (optional) is the browser-facing origin when it differs from Authority,
        // e.g. in Docker the app reaches Keycloak as keycloak:8080 but the browser needs localhost:8180.
        // We rewrite only the browser redirect URL on the fly; everything server-to-server stays internal.
        o.Authority    = oidcCfg["Authority"];
        o.ClientId     = oidcCfg["ClientId"];
        o.ClientSecret = oidcCfg["ClientSecret"];
        o.ResponseType = "code";
        o.SaveTokens   = true;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.RequireHttpsMetadata = requireHttpsMetadata;
        // When Keycloak runs behind a TLS proxy it advertises its external URL in the
        // discovery doc, so the PAR endpoint points to the public internet rather than
        // the internal Docker host.  Standard redirects are sufficient here.
        o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        o.Scope.Add("openid");
        o.Scope.Add("profile");

        o.CorrelationCookie.SameSite    = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = cookieSecurePolicy;
        o.NonceCookie.SameSite          = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy      = cookieSecurePolicy;

        o.TokenValidationParameters.ValidateIssuer   = validateIssuer;
        o.TokenValidationParameters.ValidateAudience = validateAudience;
        if (validIssuers?.Length   > 0) o.TokenValidationParameters.ValidIssuers   = validIssuers;
        if (validAudiences?.Length > 0) o.TokenValidationParameters.ValidAudiences = validAudiences;

        var publicOrigin  = oidcCfg["PublicOrigin"]?.TrimEnd('/');
        var privateOrigin = string.IsNullOrEmpty(publicOrigin)
            ? null
            : new Uri(o.Authority!).GetLeftPart(UriPartial.Authority).TrimEnd('/');

        o.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.HandleResponse();
                }
                else if (privateOrigin != null)
                {
                    ctx.ProtocolMessage.IssuerAddress =
                        ctx.ProtocolMessage.IssuerAddress.Replace(privateOrigin, publicOrigin);
                }
                return Task.CompletedTask;
            },
            OnRedirectToIdentityProviderForSignOut = ctx =>
            {
                if (privateOrigin != null)
                    ctx.ProtocolMessage.IssuerAddress =
                        ctx.ProtocolMessage.IssuerAddress.Replace(privateOrigin, publicOrigin);
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();
builder.Services.AddAuthorization(opts =>
    opts.AddPolicy("Admin", p => p.RequireRole(adminRole)));

// Persist Data Protection keys to the same directory as the vector DB so they survive
// container restarts. Without this, correlation cookies from in-flight OIDC flows become
// unreadable after a restart, causing "Correlation failed" login errors.
var dpKeysPath = Path.Combine(Path.GetDirectoryName(vectorDbPath)!, "dp-keys");
Directory.CreateDirectory(dpKeysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));

// ── AI clients ────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<IChatClient>(
    _ => (IChatClient)new OllamaApiClient(new Uri(ollamaBaseUrl), chatModel));

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    _ => (IEmbeddingGenerator<string, Embedding<float>>)new OllamaApiClient(
        new Uri(ollamaBaseUrl), embeddingModel));

// ── Vector store ──────────────────────────────────────────────────────────────

builder.Services.AddSingleton<VectorStoreCollection<string, DocumentChunk>>(
    _ => new SqliteCollection<string, DocumentChunk>(
        vectorDbConnectionString, "doc_chunks", new SqliteCollectionOptions()));

// ── Application services ──────────────────────────────────────────────────────

builder.Services.AddHttpClient("ado");
builder.Services.AddSingleton<MarkdownIngestionService>();
builder.Services.AddSingleton<AzureDevOpsIngestionService>();
builder.Services.AddSingleton<RagQueryService>();
builder.Services.AddSingleton(sp =>
    new ConversationService(
        vectorDbConnectionString,
        sp.GetRequiredService<ILogger<ConversationService>>()));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Require auth for all requests — OIDC callback (/signin-oidc) is handled by
// UseAuthentication() before this middleware runs, so it is not blocked.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
    {
        await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme);
        return;
    }
    await next(ctx);
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Startup tasks ─────────────────────────────────────────────────────────────

var convService = app.Services.GetRequiredService<ConversationService>();
await convService.EnsureTablesAsync();

using (var scope = app.Services.CreateScope())
{
    var adoCfg     = builder.Configuration.GetSection("AzureDevOps");
    var adoBaseUrl = adoCfg["BaseUrl"];

    if (!string.IsNullOrEmpty(adoBaseUrl))
    {
        var adoIngestion = scope.ServiceProvider.GetRequiredService<AzureDevOpsIngestionService>();
        try
        {
            app.Logger.LogInformation("Running startup ADO sync...");
            await adoIngestion.SyncAndIngestAsync();
            app.Logger.LogInformation("Startup ADO sync complete.");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Startup ADO sync failed — falling back to local ingestion.");
            var ingestion = scope.ServiceProvider.GetRequiredService<MarkdownIngestionService>();
            try { await ingestion.IngestAllAsync(); }
            catch (Exception ex2) { app.Logger.LogError(ex2, "Local ingestion also failed."); }
        }
    }
    else
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
}

// ── Auth endpoints ────────────────────────────────────────────────────────────

// GET /api/me — current user's identity (called by the frontend on load).
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var name    = ctx.User.FindFirstValue("preferred_username")
                 ?? ctx.User.FindFirstValue(ClaimTypes.Name)
                 ?? "Unknown";
    var sub     = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    var isAdmin = ctx.User.IsInRole(adminRole);
    return Results.Ok(new { name, sub, isAdmin });
}).RequireAuthorization();

// GET /auth/logout — sign out of both the local cookie and Keycloak.
app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
});

// ── Conversation endpoints ────────────────────────────────────────────────────

app.MapGet("/api/conversations", async (
    ConversationService conversations,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var list   = await conversations.ListConversationsAsync(userId, ct);
    return Results.Ok(list.Select(c => new
    {
        c.Id,
        c.Title,
        createdAt = c.CreatedAt.ToString("O"),
    }));
}).RequireAuthorization();

app.MapGet("/api/conversations/{id}/messages", async (
    string id,
    ConversationService conversations,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (!await conversations.BelongsToUserAsync(userId, id, ct))
        return Results.Forbid();

    var msgs = await conversations.GetMessagesAsync(id, ct);
    return Results.Ok(msgs.Select(m => new
    {
        m.Role,
        m.Content,
        createdAt = m.CreatedAt.ToString("O"),
    }));
}).RequireAuthorization();

app.MapDelete("/api/conversations/{id}", async (
    string id,
    ConversationService conversations,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (!await conversations.BelongsToUserAsync(userId, id, ct))
        return Results.Forbid();

    await conversations.DeleteConversationAsync(userId, id, ct);
    return Results.NoContent();
}).RequireAuthorization();

// ── RAG endpoints ─────────────────────────────────────────────────────────────

// POST /api/ingest — rescans the docs folder and upserts changes. Admin only.
app.MapPost("/api/ingest", async (
    MarkdownIngestionService ingestion,
    CancellationToken ct) =>
{
    await ingestion.IngestAllAsync(ct);
    return Results.Ok(new { message = "Ingestion complete." });
}).RequireAuthorization("Admin");

// POST /api/ado-webhook — receives ADO Server push events and triggers re-indexing.
app.MapPost("/api/ado-webhook", async (
    HttpRequest request,
    AzureDevOpsIngestionService adoIngestion,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("AdoWebhook");

    request.EnableBuffering();
    var body = await new StreamReader(request.Body).ReadToEndAsync(ct);

    var signature = request.Headers["X-Hub-Signature"].FirstOrDefault() ?? "";
    if (!adoIngestion.ValidateWebhookSignature(body, signature))
    {
        logger.LogWarning("ADO webhook rejected: invalid signature.");
        return Results.Unauthorized();
    }

    using var doc      = JsonDocument.Parse(body);
    var eventType = doc.RootElement.TryGetProperty("eventType", out var et) ? et.GetString() : null;
    if (eventType != "git.push")
    {
        logger.LogDebug("ADO webhook: ignoring event type '{EventType}'.", eventType);
        return Results.Ok(new { message = $"Event type '{eventType}' ignored." });
    }

    _ = Task.Run(async () =>
    {
        try { await adoIngestion.SyncAndIngestAsync(); }
        catch (Exception ex) { logger.LogError(ex, "ADO webhook sync failed."); }
    }, CancellationToken.None);

    return Results.Accepted(value: new { message = "Push received, sync started." });
});

// POST /api/chat — streams the answer as Server-Sent Events.
// Request:  { "question": "...", "conversationId": "..." | null }
// SSE:
//   data: {"t":"sources","v":[...],"conversationId":"..."}
//   data: {"t":"text","v":"token"}
//   data: [DONE]
app.MapPost("/api/chat", async (
    ChatRequest req,
    RagQueryService rag,
    ConversationService conversations,
    HttpResponse response,
    HttpContext ctx,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var logger = loggerFactory.CreateLogger("ChatEndpoint");

    if (string.IsNullOrWhiteSpace(req.Question))
    {
        response.StatusCode = 400;
        await response.WriteAsync("question is required", ct);
        return;
    }

    var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // Resolve or create the conversation, and load history.
    string conversationId;
    IReadOnlyList<ConversationMessage> priorMessages;

    if (!string.IsNullOrEmpty(req.ConversationId))
    {
        if (!await conversations.BelongsToUserAsync(userId, req.ConversationId, ct))
        {
            response.StatusCode = 403;
            return;
        }
        conversationId = req.ConversationId;
        priorMessages  = await conversations.GetMessagesAsync(conversationId, ct);
    }
    else
    {
        var conv       = await conversations.CreateConversationAsync(userId, req.Question, ct);
        conversationId = conv.Id;
        priorMessages  = [];
    }

    // Convert stored messages to the ChatMessage format the LLM understands.
    var history = priorMessages
        .Select(m => new ChatMessage(
            m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
            m.Content))
        .ToList<ChatMessage>();

    response.ContentType = "text/event-stream; charset=utf-8";
    response.Headers.CacheControl = "no-cache";
    response.Headers["X-Accel-Buffering"] = "no";

    var fullAnswer = new StringBuilder();

    try
    {
        var result = await rag.QueryAsync(req.Question, history, ct);

        var sourcesJson = JsonSerializer.Serialize(new
        {
            t = "sources",
            v = result.Sources,
            conversationId,
        }, SseJsonOptions);
        await response.WriteAsync($"data: {sourcesJson}\n\n", ct);
        await response.Body.FlushAsync(ct);

        await foreach (var token in result.AnswerStream.WithCancellation(ct))
        {
            fullAnswer.Append(token);
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
        // Save the exchange and send the 'saved' event BEFORE [DONE].
        // The client exits the SSE stream on [DONE], so anything sent after it is lost.
        if (fullAnswer.Length > 0)
        {
            try
            {
                var assistantMsgId = await conversations.SaveExchangeAsync(
                    conversationId, req.Question, fullAnswer.ToString(), CancellationToken.None);

                var savedJson = JsonSerializer.Serialize(
                    new { t = "saved", messageId = assistantMsgId }, SseJsonOptions);
                await response.WriteAsync($"data: {savedJson}\n\n", CancellationToken.None);
                await response.Body.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist conversation exchange.");
            }
        }

        await response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
        await response.Body.FlushAsync(CancellationToken.None);
    }
}).RequireAuthorization();

// POST /api/feedback — thumbs up (rating=1) or thumbs down (rating=-1) for an answer.
app.MapPost("/api/feedback", async (
    FeedbackRequest req,
    ConversationService conversations,
    CancellationToken ct) =>
{
    if (req.Rating != 1 && req.Rating != -1)
        return Results.BadRequest(new { error = "Rating must be 1 or -1." });

    await conversations.SaveFeedbackAsync(req.MessageId, req.Rating, ct);
    return Results.NoContent();
}).RequireAuthorization();

app.Run();

// ── Supporting types ──────────────────────────────────────────────────────────

record ChatRequest(string Question, string? ConversationId);
record FeedbackRequest(string MessageId, int Rating);

static partial class Program
{
    internal static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

// Maps Keycloak realm_access.roles into standard ASP.NET role claims so that
// RequireRole / [Authorize(Roles=...)] works without Keycloak-specific code at each call site.
sealed class KeycloakRolesClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var realmAccess = principal.FindFirstValue("realm_access");
        if (string.IsNullOrEmpty(realmAccess))
            return Task.FromResult(principal);

        using var doc = JsonDocument.Parse(realmAccess);
        if (!doc.RootElement.TryGetProperty("roles", out var rolesEl))
            return Task.FromResult(principal);

        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var role in rolesEl.EnumerateArray())
        {
            var roleName = role.GetString();
            if (!string.IsNullOrEmpty(roleName) && !identity.HasClaim(ClaimTypes.Role, roleName))
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
        }

        return Task.FromResult(principal);
    }
}
