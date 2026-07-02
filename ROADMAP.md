# Cortex — Development Roadmap

This document captures the current state of the application and lays out development directions toward a production-grade, publicly shareable tool.

---

## Current State

**What Cortex is:** A fully-local RAG Q&A assistant for internal documentation. It runs offline via Ollama, stores vectors in a single SQLite file, authenticates via Keycloak OIDC, and ships as a Docker Compose stack. The backend is clean, idiomatic ASP.NET Core 10 with a well-structured `Core` / `Web` separation. The frontend is a single-file vanilla HTML/JS/CSS application.

**What works well:**
- Zero cloud dependency — everything runs on local hardware
- Deterministic citations (sources retrieved before the LLM call, no hallucinated references)
- Idempotent ingestion with chunk-level upserts and deletion of stale chunks
- Conversation history with per-message feedback (👍 / 👎)
- Azure DevOps webhook-driven re-indexing
- OIDC authentication with Docker split-brain support
- Compact Docker Compose deployment (Ollama + Keycloak + app in one `docker compose up`)

**Gaps before production or public release:**
- No tests
- UI is a single HTML file — no Markdown rendering in chat, no component reuse, fragile to extend
- Several OIDC security settings are permissive (`ValidateIssuer = false`, `RequireHttpsMetadata = false`)
- No HTTPS or TLS in the Docker stack
- Re-index button is accessible to all authenticated users
- No health/readiness endpoints
- No telemetry or observability
- Startup ingestion blocks the app start (synchronous)
- No admin interface for reviewing feedback or usage
- PAT and other secrets embedded in config files rather than a secret store

---

## Direction 1 — React Frontend (Highest Impact on UX)

The single HTML file is the most visible gap when sharing externally. A React rewrite enables proper Markdown rendering in chat, better component reuse, and a testable UI layer.

**Recommended stack:**
- **Vite + React 19 + TypeScript** — fast dev server, small bundle, no bundler config overhead
- **react-markdown + rehype-highlight** — renders the LLM's Markdown output (code blocks, lists, bold) correctly inside chat bubbles
- **Tailwind CSS** — replaces the hand-rolled CSS, consistent design tokens
- **Zustand** — minimal state management for conversations / messages
- **@tanstack/react-query** — async data fetching with caching and refetch for conversations list

**Approach:** Keep the existing ASP.NET backend unchanged. Add a Vite project in `src/RagAssistant.Web/ClientApp/` and configure `dotnet run` to proxy API requests to it in development. In production, Vite's build output is served as static files by ASP.NET (same pattern as `dotnet new react`).

**Key UI improvements the rewrite unlocks:**
- Markdown rendering (code blocks, tables, lists in LLM answers)
- Collapsible conversation history with search
- Source pane that highlights the exact retrieved passage
- Keyboard shortcuts (⌘K to focus, ⌘N for new chat)
- Dark mode toggle
- Mobile-friendly layout (sidebar as a drawer on small screens)
- Accessible focus management throughout

---

## Direction 2 — Production Hardening

Items required before using this with a real team, in priority order.

### Security

| Issue | Fix |
|-------|-----|
| `RequireHttpsMetadata = false` | Enable for production; add a flag for `Development` override only |
| `ValidateIssuer = false` / `ValidateAudience = false` | Set both to `true` and configure `ValidIssuers` / `ValidAudiences` |
| Re-index endpoint open to all users | Add an `admin` role check via Keycloak roles claim |
| ADO PAT in config | Use ASP.NET Data Protection or pull from environment secrets at runtime |
| HMAC-SHA1 for ADO webhook | Acceptable for ADO Server compatibility; document the limitation |
| No HTTPS in Docker stack | Add an nginx reverse proxy service in `docker-compose.yml` with a self-signed cert for LAN use, or a Let's Encrypt Certbot sidecar |

### Reliability

- **Health endpoints** — add `/health` (liveness) and `/health/ready` (readiness, checks Ollama reachability) using `Microsoft.AspNetCore.Diagnostics.HealthChecks`. The Docker Compose `app` service healthcheck should call `/health/ready`.
- **Async startup ingestion** — move ingestion out of the startup path into a `BackgroundService`. The app should serve traffic immediately; ingestion runs in background and exposes a status flag on `/health/ready` or a dedicated `/api/ingest/status` endpoint.
- **Concurrency control on SQLite** — the current `ConversationService` opens a new connection per call, which can hit SQLite WAL limits under concurrent load. Switch to a connection pool or a dedicated singleton connection with a semaphore, or migrate to `Microsoft.Data.Sqlite` in WAL mode with `Pooling=True`.
- **Rate limiting** — add `dotnet-ratelimiting` middleware: per-user sliding-window limiter on `/api/chat` to prevent accidental hammering of the Ollama GPU.

### Configuration

- Document all environment variables in `README.md` with a `docker-compose.override.yml` example for secrets
- Add `appsettings.Production.json` that enables HTTPS redirection, strict OIDC validation, and structured logging

---

## Direction 3 — Observability & Admin

### Telemetry

- **OpenTelemetry** — add `OpenTelemetry.AspNetCore` and export traces to an OTLP collector (e.g. Grafana Alloy or the OpenTelemetry Collector in Docker Compose). Instrument the RAG query path so you can see embedding time, vector search time, and LLM streaming time as separate spans.
- **Structured logging** — replace the default console logger with Serilog + Serilog.Sinks.Console using `JsonFormatter` for log aggregation compatibility.
- **Prometheus metrics** — expose `/metrics` with request count, query latency percentiles, and ingestion duration.

### Feedback Dashboard

The `feedback` table already stores thumbs up/down per message. Currently, no one can see this data without querying SQLite directly. Build a simple read-only `/admin` page (React, role-gated) showing:

- Top-rated questions and their answers
- Low-rated questions (where the RAG pipeline failed)
- Query volume over time
- Most-cited source files

This data is the primary feedback signal for improving the RAG pipeline.

---

## Direction 4 — RAG Quality Improvements

The core retrieval pipeline is solid. These improvements increase answer quality, especially as the doc corpus grows.

### Hybrid Search (BM25 + Vector)

Pure cosine similarity on embeddings misses exact keyword matches. Combine vector search with BM25 full-text search (available natively in SQLite via FTS5) and merge results using Reciprocal Rank Fusion (RRF). This typically improves recall on acronym-heavy internal docs significantly.

### Reranking

After retrieving `TopK * 2` candidates, run a cross-encoder reranker (e.g. `cross-encoder/ms-marco-MiniLM-L-6-v2` via Ollama or a local ONNX model) to re-score and drop the weakest chunks before sending to the LLM. Reduces noise in the LLM context window.

### Query Expansion / HyDE

For short or ambiguous questions, generate a hypothetical answer first (Hypothetical Document Embedding) and embed *that* for retrieval. Improves recall for questions phrased differently from the documentation language.

### Context Window Management

Long conversation histories grow the prompt unboundedly. Add a sliding-window or summary-based history truncation strategy so older turns are compressed rather than dropped or sent verbatim.

### Metadata Filters

Add pre-filter support in the query path: allow users to scope search to a specific tag, owner, or last-verified date from the front-end. The `DocumentChunk` model already has `Tags` and `LastVerified` — they just aren't used in filtering today.

---

## Direction 5 — Document Source Connectors

The connector model (ADO → `MarkdownIngestionService`) is clean. Add connectors for:

| Source | Priority | Notes |
|--------|----------|-------|
| **GitHub / GitLab** | High | Same PAT + HTTP pattern as ADO; webhook on push events |
| **PDF** | High | Many internal docs are PDF; use `PdfPig` or `iText` to extract text |
| **Confluence Cloud** | Medium | REST API; export as Markdown or parse HTML content |
| **SharePoint / OneDrive** | Medium | Microsoft Graph SDK |
| **Notion** | Low | Notion SDK; good for product/design teams |

Each connector should implement a `IDocumentSource` interface with `SyncAsync()` returning `IAsyncEnumerable<RawDocument>`, and the ingestion service should embed + upsert from any source uniformly.

---

## Direction 6 — Testing

Zero tests is the biggest risk for a team tool. Introduce tests in priority order:

1. **Unit tests for `MarkdownIngestionService`** — chunking logic, front matter parsing, heading breadcrumb generation. Pure functions, no dependencies; fast to write and run.
2. **Unit tests for `RagQueryService`** — mock `IEmbeddingGenerator` and `VectorStoreCollection`; assert that sources are built correctly and history is injected into messages.
3. **Integration tests for `ConversationService`** — spin up an in-memory SQLite DB; test create, get, delete, feedback, and concurrency.
4. **HTTP integration tests** — use `WebApplicationFactory<Program>` with a test Ollama stub to test the full chat SSE streaming endpoint, auth enforcement (401 on unauthenticated), and webhook signature validation.

Use **xUnit** + **Moq** (or `NSubstitute`) + **FluentAssertions**. Add a `src/RagAssistant.Tests/` project.

---

## Direction 7 — GitHub / OSS Readiness

If the goal is to share this publicly, the repo needs:

- **`LICENSE`** — MIT is the lowest-friction choice for a dev tool
- **`CONTRIBUTING.md`** — how to set up the dev environment, coding conventions, PR process
- **GitHub Actions CI** — `dotnet build` + `dotnet test` + `npm run build` on every PR; Docker build test on main
- **Issue templates** — bug report and feature request templates in `.github/ISSUE_TEMPLATE/`
- **Social preview image** — a screenshot or demo GIF in the README makes a huge difference on GitHub and LinkedIn
- **Rename or align branding** — the title `"Internal Docs Assistant"` is hardcoded in `index.html`; make it configurable via `appsettings.json` (e.g. `App:Name`) so deployers can brand their instance
- **Published Docker image** — push to GitHub Container Registry (`ghcr.io`) on release tags so others can `docker compose up` without building locally

---

## Priority Summary

| Priority | Direction | Why |
|----------|-----------|-----|
| 1 | React UI | Biggest quality-of-life delta; unblocks Markdown rendering |
| 2 | Security hardening | Required before giving real team access |
| 3 | Health endpoints + async startup | Required for reliable container operation |
| 4 | Tests | Guards against regressions as the codebase grows |
| 5 | Observability | Needed once real users are on it |
| 6 | Hybrid search + reranking | Measurable answer quality improvement |
| 7 | OSS readiness | Required before GitHub/LinkedIn publish |
| 8 | Additional connectors | Scope creep risk — add only what the team actually uses |