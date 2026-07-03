# Cortex — Development Roadmap

This document captures the current state of the application and the prioritised path toward a production-grade, publicly shareable tool.

---

## Current State

**What Cortex is:** A fully-local RAG Q&A assistant for internal documentation. Runs offline via Ollama, stores vectors in SQLite, authenticates via Keycloak OIDC, and ships as a Docker Compose stack. Backend is clean, idiomatic ASP.NET Core 10 with a `Core` / `Web` separation. Frontend is React 19 + Vite + Tailwind CSS, served as a static SPA from ASP.NET's `wwwroot`.

**What works today:**
- Zero cloud dependency — everything runs on local hardware
- Deterministic citations (sources retrieved before the LLM call, no hallucinated references)
- Idempotent ingestion with chunk-level upserts and deletion of stale chunks
- **Hybrid retrieval** — vector search + BM25 (SQLite FTS5) merged with Reciprocal Rank Fusion; optional HyDE and LLM reranking behind config flags
- **Tag scoping** — front-matter tags are wired as a retrieval pre-filter, settable from the chat UI
- Sliding-window conversation history so long chats don't blow the context window
- SSE streaming chat responses with Markdown rendering, syntax highlighting, and per-code-block copy buttons on a high-contrast (always-dark) code surface
- Inline citation footnotes `[^N]` that open a source passage modal with keyword highlighting
- Thumbs-up / thumbs-down feedback per assistant message + admin feedback audit endpoint
- Conversation history with per-user isolation
- **Connectors** — `IDocumentSource` abstraction with Azure DevOps and GitHub implementations (webhook-triggered re-indexing for both), plus PDF ingestion via PdfPig
- OIDC authentication (cookie-based server-side flow) via Keycloak, admin role gating, hardened production config
- OpenTelemetry traces / metrics / logs to a bundled Aspire Dashboard container
- Health/readiness endpoints, background startup ingestion, per-user rate limiting, WAL-mode SQLite
- Three compose profiles (`local`, `codespaces`, `production`) with per-environment `appsettings.{Env}.json`; nginx TLS proxy in production

**Gaps before public release:**
- No tests (Direction 8)
- No OSS packaging — license, CI, published image (Direction 9)
- Connectors for Confluence / SharePoint / Notion not yet implemented (deferred until needed)

---

## Direction 1 — Observability & Audit  ✅ Done

Shipped: standalone Aspire Dashboard container in the Compose stack; OpenTelemetry tracing/metrics/logging over OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT`); custom spans and metrics for embedding, vector search, BM25, HyDE, reranking, chat, and ingestion (`Telemetry.cs`); structured JSON console logging; read-only admin feedback audit at `/api/admin/feedback`.

---

## Direction 2 — Clean Environment Separation  ✅ Done

Shipped: `appsettings.json` (shared defaults, no secrets) + `Development` / `Codespaces` / `Production` overlays; `ASPNETCORE_ENVIRONMENT` set per Compose profile (`local`, `codespaces`, `production`); secrets pulled from `.env` (documented in `.env.example`); nginx TLS reverse proxy for the production profile with `scripts/gen-certs.sh` for self-signed certs.

---

## Direction 3 — UI Polish  ✅ Done

Shipped: auto-expanding textarea (Enter/Shift+Enter), asymmetric message layout, blinking streaming cursor, three-dot typing indicator, empty-state prompt suggestions, sidebar with relative timestamps + ⌘K search + ⌘N new chat, collapsible source panel with preview cards, system-aware dark mode, mobile drawer sidebar, smart auto-scroll with "scroll to bottom" button, per-message copy button, per-code-block copy button with language label, always-dark high-contrast code blocks (github-dark palette in both themes), focus management and `aria-live` streaming region.

---

## Direction 4 — Security Hardening  ✅ Done

Shipped: strict OIDC validation (`RequireHttpsMetadata`, `ValidateIssuer`, `ValidateAudience`) in production config, relaxed only in `Development`/`Codespaces`; `cortex-admin` role required for `/api/ingest`; PAT/secrets from environment variables; nginx TLS in production; HMAC-SHA1 ADO webhook limitation documented in code (GitHub webhook uses HMAC-SHA256).

---

## Direction 5 — Reliability  ✅ Done

Shipped: `/health` (liveness) and `/health/ready` (readiness with Ollama + ingestion checks) wired into the Compose healthcheck; startup ingestion moved to a `BackgroundService` with status on `/api/ingest/status`; SQLite WAL mode; per-user sliding-window rate limiting on `/api/chat`.

---

## Direction 6 — RAG Quality Improvements  ✅ Done

All five improvements are implemented in `RagQueryService`:

| Feature | Status | Config |
|---|---|---|
| Hybrid Search (BM25 via FTS5 + vector, RRF merge) | **On by default** | `Rag:HybridSearch`, `Rag:RrfK` |
| LLM reranking of TopK×2 candidates | Off by default (extra LLM round-trip) | `Rag:Reranking:Enabled`, `Rag:Reranking:CandidateMultiplier` |
| HyDE for short questions | Off by default (extra LLM round-trip) | `Rag:Hyde:Enabled`, `Rag:Hyde:MaxQuestionLength` |
| Sliding-window history truncation | On (12 messages) | `Rag:MaxHistoryMessages` |
| Metadata (tag) pre-filters from the front-end | On — "Scope to tag" input in the chat UI | — |

Notes: the FTS5 index (`chunks_fts`) lives in the same SQLite file as the vector table and is kept in sync by the ingestion pipeline. Reranking uses the local chat model rather than a dedicated cross-encoder — revisit with a local ONNX cross-encoder if reranking quality matters at scale. `LastVerified` recency filtering is stored per chunk but not yet exposed in the UI.

---

## Direction 7 — Document Source Connectors  ◐ Partially done

`IDocumentSource` (`SyncAsync()` → `IAsyncEnumerable<RawDocument>`) + `DocumentSourceSynchronizer` (mirror to cache folder, delete stale, ingest) are in place.

| Source | Status | Notes |
|--------|--------|-------|
| **Azure DevOps** | ✅ Done | PAT + items API; HMAC-SHA1 webhook on `/api/ado-webhook` |
| **PDF** | ✅ Done | PdfPig text extraction; pages become `## Page N` sections, so citations carry page numbers |
| **GitHub / GitHub Enterprise** | ✅ Done | PAT + trees/contents API; HMAC-SHA256 push webhook on `/api/github-webhook` |
| **Confluence Cloud** | Deferred | REST API; export as Markdown or parse HTML — add when a team needs it |
| **SharePoint / OneDrive** | Deferred | Microsoft Graph SDK — add when a team needs it |
| **Notion** | Deferred | Notion SDK — add when a team needs it |

---

## Direction 8 — Testing  ⬜ Remaining

Zero tests is the biggest regression risk as the codebase grows. Add in priority order:

1. **Unit tests for `MarkdownIngestionService`** — chunking logic, front-matter parsing, heading breadcrumbs. Pure functions, no deps; fast to write.
2. **Unit tests for `RagQueryService`** — mock `IEmbeddingGenerator`, `IChatClient`, and `VectorStoreCollection`; assert RRF fusion order, tag filtering, history windowing, and source building.
3. **Unit tests for `FullTextIndex`** — in-memory SQLite; FTS5 query sanitisation (punctuation, quotes) and upsert/delete sync.
4. **Integration tests for `ConversationService`** — in-memory SQLite; test create/get/delete/feedback and concurrency.
5. **HTTP integration tests** — `WebApplicationFactory<Program>` with a stubbed Ollama; test SSE streaming, 401 on unauthenticated requests, and both webhook signature validations (SHA1 + SHA256).

Stack: **xUnit** + **NSubstitute** + **FluentAssertions** in a `src/RagAssistant.Tests/` project.

---

## Direction 9 — GitHub / OSS Readiness  ⬜ Remaining

Required before publishing:

- **`LICENSE`** — MIT
- **`CONTRIBUTING.md`** — dev environment setup, coding conventions, PR process
- **GitHub Actions CI** — `dotnet build` + `dotnet test` + `npm run build` on every PR; Docker build test on `main`
- **Issue templates** — bug report and feature request in `.github/ISSUE_TEMPLATE/`
- **Configurable app name** — `"Cortex"` is hardcoded in `index.html` / `Header.tsx`; make it `App:Name` in `appsettings.json`
- **Published Docker image** — push to GitHub Container Registry (`ghcr.io`) on release tags so others can `docker compose up` without building locally
- **Social preview** — a screenshot or short demo GIF in the README

---

## Priority Summary

| # | Direction | Status |
|---|---|---|
| 1 | React UI | ✅ Done |
| 2 | Observability & Audit | ✅ Done |
| 3 | Environment separation | ✅ Done |
| 4 | UI polish | ✅ Done |
| 5 | Security hardening | ✅ Done |
| 6 | Reliability | ✅ Done |
| 7 | RAG quality | ✅ Done (reranker/HyDE off by default — enable after measuring on real usage) |
| 8 | Document connectors | ◐ ADO + GitHub + PDF done; Confluence/SharePoint/Notion as needed |
| 9 | Testing | ⬜ **Next** — before accepting outside contributions |
| 10 | OSS readiness | ⬜ Last — polish and publish once tests are in |
