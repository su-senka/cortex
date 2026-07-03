# Cortex — Development Roadmap

This document captures the current state of the application and the prioritised path toward a production-grade, publicly shareable tool.

---

## Current State

**What Cortex is:** A fully-local RAG Q&A assistant for internal documentation. Runs offline via Ollama, stores vectors in SQLite, authenticates via Keycloak OIDC, and ships as a Docker Compose stack. Backend is clean, idiomatic ASP.NET Core 10 with a `Core` / `Web` separation. Frontend is React 19 + Vite + Tailwind CSS, served as a static SPA from ASP.NET's `wwwroot`.

**What works today:**
- Zero cloud dependency — everything runs on local hardware
- Deterministic citations (sources retrieved before the LLM call, no hallucinated references)
- Idempotent ingestion with chunk-level upserts and deletion of stale chunks
- SSE streaming chat responses with Markdown rendering and syntax highlighting
- Inline citation footnotes `[^N]` that open a source passage modal with keyword highlighting
- Thumbs-up / thumbs-down feedback per assistant message
- Conversation history with per-user isolation
- Azure DevOps webhook-triggered re-indexing; admin-only `/api/ingest` endpoint
- OIDC authentication (cookie-based server-side flow) via Keycloak
- Two launch environments: `./start-local.sh` (localhost) and `./start-codespaces.sh` (GitHub Codespaces, dynamic redirect URI registration)
- React UI confirmed running with auth, chat streaming, conversation history, and citations all wired up

**Gaps before production or public release:**
- No observability — no traces, no structured logs, no metrics
- Environment config scattered between shell scripts and `appsettings.json`; no clean `Development` / `Codespaces` / `Production` config layer
- UI functional but not visually polished; missing modern chat-app UX patterns
- Several OIDC security settings are permissive (`ValidateIssuer = false`, `RequireHttpsMetadata = false`)
- No HTTPS or TLS in the Docker stack
- PAT and other secrets embedded in config files
- No health / readiness endpoints
- Startup ingestion blocks app start (synchronous)
- No tests

---

## Direction 1 — Observability & Audit  *(do first)*

The single highest-leverage investment for understanding what the app is doing — and for debugging the RAG pipeline when answers are wrong.

### Recommended approach: .NET Aspire Dashboard (standalone)

Run the Aspire Dashboard as an extra container in `docker-compose.yml`. It is a fully self-contained OpenTelemetry viewer (traces, structured logs, metrics) with no Prometheus, Grafana, or Collector required. The app sends OTLP over gRPC; the dashboard displays it. No changes to the app host model or the Compose orchestration are needed.

```yaml
# docker-compose.yml addition
aspire-dashboard:
  image: mcr.microsoft.com/dotnet/aspire-dashboard:latest
  ports:
    - "18888:18888"   # Dashboard UI
    - "18889:18889"   # OTLP gRPC receiver
  environment:
    DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS: "true"
```

Wire the app:

```csharp
// Program.cs
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithLogging(l => l.AddOtlpExporter());
```

Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889` in the app's Compose environment.

### What to instrument

| Span / metric | Why |
|---|---|
| Embedding call (duration) | Catch slow nomic-embed-text pulls |
| Vector search (duration + result count) | Diagnose retrieval misses |
| LLM streaming call (time-to-first-token, total duration) | Baseline model latency |
| `/api/chat` request (end-to-end) | User-visible latency |
| Ingestion run (docs processed, chunks upserted/deleted) | Catch silently failing re-indexes |

### Structured logging

Replace the default console logger with **Serilog** + `Serilog.Sinks.Console` using `JsonFormatter`. The Aspire Dashboard ingests structured logs natively — every `ILogger.LogInformation(...)` call becomes a searchable, filterable record in the UI.

### Feedback audit view

The `feedback` table stores thumbs-up/down per message. Add a read-only `/api/admin/feedback` endpoint (role-gated to `admin`) returning aggregated stats: top-rated questions, low-rated questions, query volume over time, most-cited files. This surfaces the primary signal for RAG quality improvement without building a separate admin UI yet.

---

## Direction 2 — Clean Environment Separation  *(do second)*

Currently, environment-specific config is driven by shell scripts and commented-out `appsettings.json` blocks. Replace this with a proper ASP.NET config layer so each environment is self-describing and composable.

### Config layer target

| File | Purpose |
|---|---|
| `appsettings.json` | Shared defaults; no secrets, no hostnames |
| `appsettings.Development.json` | Local dev overrides (`localhost`, `RequireHttpsMetadata = false`, etc.) |
| `appsettings.Codespaces.json` | Codespaces overrides; consumed when `ASPNETCORE_ENVIRONMENT=Codespaces` |
| `appsettings.Production.json` | Strict OIDC validation, HTTPS redirection, structured logging |
| `docker-compose.override.yml` | Host-level secrets injected as env vars, never committed |

### Actions

- Set `ASPNETCORE_ENVIRONMENT` in each Compose profile (`development`, `codespaces`, `production`) rather than in shell scripts
- Replace the `start-local.sh` / `start-codespaces.sh` divergence with `docker compose --profile local up` and `docker compose --profile codespaces up`
- Pull the Keycloak admin password, ADO PAT, and webhook secret from environment variables (already in `.env`) rather than hardcoded in `appsettings.json`
- Add `appsettings.Production.json` that sets `ValidateIssuer = true`, `ValidateAudience = true`, `ValidIssuers`, `ValidAudiences`, and `RequireHttpsMetadata = true`
- Document all environment variables in `README.md` with a `docker-compose.override.yml` example

### HTTPS for production

Add an **nginx** reverse-proxy service to the Compose stack for production:
- Self-signed cert for LAN/intranet use (generated once, mounted as a volume)
- Certbot sidecar for public deployments with a real domain

---

## Direction 3 — UI Polish  *(do third)*

The UI is functional but visually dated compared to modern chat products. The goal here is not a redesign — it is a targeted set of improvements that lift perceived quality to the level of ChatGPT / Claude.ai without changing the architecture.

### High-impact changes

| Area | Change |
|---|---|
| **Chat input** | Auto-expanding textarea (grows to ~5 lines, then scrolls); submit on `Enter`, newline on `Shift+Enter` |
| **Message bubbles** | User messages right-aligned with a distinct background; assistant messages full-width with no bubble; remove visual symmetry |
| **Streaming cursor** | Blinking `▋` appended during token streaming; disappears when `[DONE]` arrives |
| **Typing / loading state** | Three-dot animated indicator between user message and first token |
| **Empty state** | Centred prompt suggestions on a fresh conversation ("What is the VPN setup process?", "How do I rotate a TLS cert?") |
| **Sidebar** | Conversation list with relative timestamps; keyboard shortcut `⌘K` to focus the search input |
| **New conversation** | Sticky `+ New chat` button at top of sidebar; `⌘N` shortcut |
| **Source panel** | Collapsible; show a preview card (file path + first 2 lines of the passage) before the user opens the modal |
| **Dark mode** | System-preference-respecting `prefers-color-scheme` toggle stored in `localStorage` |
| **Mobile layout** | Sidebar as a slide-in drawer; source panel hidden by default on small screens |
| **Scrolling** | Auto-scroll to bottom during streaming; "Scroll to bottom" button appears when user scrolls up mid-stream |
| **Copy button** | Per-message copy-to-clipboard button (appears on hover) |

### Accessibility

- Focus the chat input automatically after page load and after each response completes
- `aria-live="polite"` region for streaming tokens so screen readers announce new content
- All interactive elements keyboard-navigable

---

## Direction 4 — Security Hardening  *(do alongside Direction 2 for prod)*

| Issue | Fix |
|---|---|
| `RequireHttpsMetadata = false` | Enable in `appsettings.Production.json`; keep `false` in `Development` only |
| `ValidateIssuer = false` / `ValidateAudience = false` | Set both to `true` in production config; configure `ValidIssuers` / `ValidAudiences` |
| Re-index endpoint open to all users | Add an `admin` Keycloak role claim check on `/api/ingest` |
| ADO PAT in config | Read from environment variable at runtime; document in `.env.example` |
| No HTTPS in Docker stack | nginx reverse proxy (see Direction 2) |
| HMAC-SHA1 for ADO webhook | Acceptable for ADO Server compatibility; add a comment documenting the limitation |

---

## Direction 5 — Reliability  *(do before exposing to a team)*

- **Health endpoints** — add `/health` (liveness) and `/health/ready` (readiness, checks Ollama reachability) using `Microsoft.AspNetCore.Diagnostics.HealthChecks`. Wire the Docker Compose `healthcheck` to call `/health/ready`.
- **Async startup ingestion** — move ingestion out of `Program.cs` into a `BackgroundService`. App serves traffic immediately; ingestion runs in the background and exposes status on `/health/ready` or a dedicated `/api/ingest/status`.
- **SQLite concurrency** — the current `ConversationService` opens a new connection per call. Switch to a singleton connection in WAL mode (`Pooling=True; Journal Mode=WAL`) or add a `SemaphoreSlim(1,1)` guard.
- **Rate limiting** — add `dotnet-ratelimiting` per-user sliding-window limiter on `/api/chat` to prevent accidental GPU hammering.

---

## Direction 6 — RAG Quality Improvements

The retrieval pipeline is solid. These improvements increase answer quality as the doc corpus grows.

### Hybrid Search (BM25 + Vector)

Pure cosine similarity misses exact keyword matches. Combine vector search with BM25 full-text search (SQLite FTS5) and merge results with Reciprocal Rank Fusion (RRF). Improves recall on acronym-heavy internal docs significantly.

### Reranking

After retrieving `TopK × 2` candidates, run a cross-encoder reranker (`cross-encoder/ms-marco-MiniLM-L-6-v2` via Ollama or a local ONNX model) to re-score and drop the weakest chunks before sending context to the LLM.

### HyDE (Hypothetical Document Embedding)

For short or ambiguous questions, generate a hypothetical answer first and embed *that* for retrieval. Improves recall when questions are phrased differently from the documentation language.

### Context Window Management

Add a sliding-window or summary-based history truncation strategy so older turns are compressed rather than dropped or sent verbatim as the conversation grows.

### Metadata Filters

`DocumentChunk` already has `Tags` and `LastVerified`. Wire them as pre-filters in the query path so users can scope search to a specific tag or recency from the front-end.

---

## Direction 7 — Document Source Connectors

The connector model (ADO → `MarkdownIngestionService`) is clean. Add connectors in this order:

| Source | Priority | Notes |
|--------|----------|-------|
| **PDF** | High | Many internal docs are PDF; use `PdfPig` to extract text |
| **GitHub / GitLab** | High | Same PAT + HTTP pattern as ADO; webhook on push events |
| **Confluence Cloud** | Medium | REST API; export as Markdown or parse HTML |
| **SharePoint / OneDrive** | Medium | Microsoft Graph SDK |
| **Notion** | Low | Notion SDK |

Each connector should implement `IDocumentSource` with `SyncAsync()` returning `IAsyncEnumerable<RawDocument>`.

---

## Direction 8 — Testing

Zero tests is the biggest regression risk as the codebase grows. Add in priority order:

1. **Unit tests for `MarkdownIngestionService`** — chunking logic, front-matter parsing, heading breadcrumbs. Pure functions, no deps; fast to write.
2. **Unit tests for `RagQueryService`** — mock `IEmbeddingGenerator` and `VectorStoreCollection`; assert source building and history injection.
3. **Integration tests for `ConversationService`** — in-memory SQLite; test create/get/delete/feedback and concurrency.
4. **HTTP integration tests** — `WebApplicationFactory<Program>` with a stubbed Ollama; test SSE streaming, 401 on unauthenticated requests, and webhook signature validation.

Stack: **xUnit** + **NSubstitute** + **FluentAssertions** in a `src/RagAssistant.Tests/` project.

---

## Direction 9 — GitHub / OSS Readiness

Required before publishing:

- **`LICENSE`** — MIT
- **`CONTRIBUTING.md`** — dev environment setup, coding conventions, PR process
- **GitHub Actions CI** — `dotnet build` + `dotnet test` + `npm run build` on every PR; Docker build test on `main`
- **Issue templates** — bug report and feature request in `.github/ISSUE_TEMPLATE/`
- **Configurable app name** — `"Internal Docs Assistant"` is hardcoded in `index.html`; make it `App:Name` in `appsettings.json`
- **Published Docker image** — push to GitHub Container Registry (`ghcr.io`) on release tags so others can `docker compose up` without building locally
- **Social preview** — a screenshot or short demo GIF in the README

---

## Priority Summary

| # | Direction | Status | Why now |
|---|---|---|---|
| 1 | ~~React UI~~ | **Done** | — |
| 2 | Observability & Audit | **Next** | Can't improve what you can't see; Aspire Dashboard is low-effort, high-value |
| 3 | Environment separation | **Next** | Unblocks clean production config and removes shell-script hacks |
| 4 | UI polish | After envs | Improves perceived quality for demos and team rollout |
| 5 | Security hardening | With env work | Shares config layer with Direction 3; no standalone effort |
| 6 | Reliability | Before team use | Health checks + async ingestion + rate limits |
| 7 | RAG quality | After team use | Real usage data drives what to fix first |
| 8 | Document connectors | As needed | Add only what the team actually uses |
| 9 | Testing | Before OSS | Prevents regressions when contributors join |
| 10 | OSS readiness | Last | Polish and publish once the product is stable |
