# Cortex — Internal Docs Q&A (Local RAG)

[![CI](https://github.com/su-senka/cortex/actions/workflows/ci.yml/badge.svg)](https://github.com/su-senka/cortex/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![React 19](https://img.shields.io/badge/React-19-61DAFB)](https://react.dev/)

A fully local RAG (Retrieval-Augmented Generation) assistant that lets you ask plain-English questions about internal documentation (Markdown and PDF). Runs entirely offline on CPU via a local [Ollama](https://ollama.com) instance — no cloud calls, no external vector database.

## Architecture

```
React SPA  (Vite + Tailwind, served from wwwroot)
      │  POST /api/chat  →  Server-Sent Events
      ▼
ASP.NET Core Minimal API  (RagAssistant.Web)
      │
      ├─ MarkdownIngestionService   ← parses .md/.pdf, chunks, embeds, upserts
      ├─ RagQueryService            ← hybrid retrieval (vector + BM25/RRF), streams LLM answer
      ├─ IDocumentSource connectors ← Azure DevOps, GitHub (webhook-triggered sync)
      │
      ├─ OllamaSharp (OllamaApiClient)   as IChatClient + IEmbeddingGenerator
      ├─ Microsoft.SemanticKernel.Connectors.SqliteVec   (sqlite-vec, single .db file)
      └─ SQLite FTS5                 (BM25 keyword index, same .db file)
```

**Key design choices:**
- All models run locally via Ollama — zero cloud dependency.
- Vector store is a single SQLite file using the [sqlite-vec](https://github.com/asg017/sqlite-vec) extension — no external process needed. The BM25 keyword index (FTS5) lives in the same file.
- Retrieval is hybrid: cosine similarity + BM25 merged with Reciprocal Rank Fusion, so exact keywords and acronyms are found even when embeddings miss them. Optional HyDE and LLM reranking are available behind config flags.
- Sources are determined by retrieval before the LLM call, so citations are exact and deterministic (the model cannot hallucinate references).
- Chunk keys are deterministic (`filename#index`) so re-ingesting edited files is an upsert, not a duplicate.

---

## 1. Prerequisites

### Ollama

Install Ollama from [https://ollama.com/download](https://ollama.com/download) and pull the required models:

```bash
# Chat model — ~4.5 GB, Q4 quantised, good quality on CPU
ollama pull qwen2.5:7b-instruct-q4_K_M

# Embedding model — ~274 MB
ollama pull nomic-embed-text
```

Ollama must be running before the app starts (`ollama serve` runs automatically on macOS/Windows after install; on Linux you may need to start it manually).

Both models are configurable in `appsettings.json`. Any Ollama-compatible model works — adjust the `[VectorStoreVector(Dimensions: N)]` attribute in `DocumentChunk.cs` if you switch to an embedding model with a different output dimension.

### .NET SDK

[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

---

## 2. Running

The app ships as a Docker Compose stack (app + Ollama + Keycloak + Aspire Dashboard).
One compose file covers three environments, selected by profile:

```bash
./start-local.sh          # = docker compose --profile local up -d --build
./start-codespaces.sh     # GitHub Codespaces (also registers the session's redirect URIs)
docker compose --profile production up -d --build   # behind the nginx TLS proxy
```

Open **http://localhost:8080** (local profile). Keycloak admin is at http://localhost:8180, and the Aspire Dashboard (traces/logs/metrics) at http://localhost:18888.

For backend-only development you can still run bare-metal (`dotnet run --project src/RagAssistant.Web`), but Ollama and Keycloak must be reachable — easiest is to start the compose stack and stop just the app container. The SPA is not checked in; build it once first (`cd src/RagAssistant.Web/ClientApp && npm install && npm run build`), or use `npm run dev` for hot reload.

On startup, the app indexes the docs folder in the background — it serves traffic immediately, and `GET /api/ingest/status` (or `/health/ready`) reports ingestion progress. Ask something like:
- "How do I connect to the VPN from macOS?"
- "What are the steps to generate a TLS certificate?"
- "What is a P1 incident?"

### Indexing Your Own Docs

Point `Rag:DocsFolder` in `appsettings.json` at your Markdown folder:

```json
"Rag": {
  "DocsFolder": "C:\\internal-docs",
  ...
}
```

Click **↺ Re-index docs** in the UI, or call `POST /api/ingest`. The app rescans the folder and upserts any changes. It also removes chunks from files that have been deleted.

**Source format:** Markdown files with optional YAML front matter:

```yaml
---
title: My Document Title
tags: [tag1, tag2]
owner: Team Name
last_verified: 2025-06-01
---

# Content starts here
```

All fields are optional — the file name is used as the title if none is provided.

---

## 3. Configuration Reference

### Config layering

Settings resolve in the standard ASP.NET order — later layers override earlier ones:

| Layer | Purpose |
|-------|---------|
| `appsettings.json` | Shared defaults; no secrets, no environment hostnames |
| `appsettings.Development.json` | Local dev — relaxed OIDC (`RequireHttpsMetadata=false`, `ValidateIssuer=false`) |
| `appsettings.Codespaces.json` | Codespaces — same relaxations (TLS proxy in front, internal HTTP Keycloak) |
| `appsettings.Production.json` | Strict OIDC validation (`RequireHttpsMetadata`, `ValidateIssuer`, `ValidateAudience` all `true`) |
| Environment variables | Deploy-time wiring (hostnames) and secrets, using the `__` separator |

Each compose profile sets `ASPNETCORE_ENVIRONMENT` (`Development` / `Codespaces` / `Production`) so the right layer applies automatically.

### App settings

| Key | Default | Description |
|-----|---------|-------------|
| `App:Name` | `Cortex` | Name shown in the header and browser tab |
| `Ollama:BaseUrl` | `http://localhost:11434` | Ollama API URL |
| `Ollama:ChatModel` | `qwen2.5:7b-instruct-q4_K_M` | Model for answering questions |
| `Ollama:EmbeddingModel` | `nomic-embed-text` | Model for generating embeddings |
| `Rag:DocsFolder` | `../../docs` | Folder containing `.md` and `.pdf` files |
| `Rag:VectorDbPath` | `rag_store.db` | SQLite vector database file (relative to app ContentRoot) |
| `Rag:ChunkSize` | `1500` | Max characters per chunk |
| `Rag:ChunkOverlap` | `150` | Characters of overlap between consecutive chunks |
| `Rag:TopK` | `5` | Number of chunks retrieved per query |
| `Rag:HybridSearch` | `true` | Merge BM25 (SQLite FTS5) with vector search via Reciprocal Rank Fusion |
| `Rag:RrfK` | `60` | RRF constant — larger values flatten rank differences |
| `Rag:MaxHistoryMessages` | `12` | Sliding-window limit on conversation history sent to the LLM |
| `Rag:Hyde:Enabled` | `false` | HyDE: embed a hypothetical answer for short questions (extra LLM call) |
| `Rag:Hyde:MaxQuestionLength` | `100` | Questions at or below this length use HyDE |
| `Rag:Reranking:Enabled` | `false` | LLM reranking of TopK×multiplier candidates (extra LLM call) |
| `Rag:Reranking:CandidateMultiplier` | `2` | Over-fetch factor for rerank candidates |
| `RateLimiting:ChatPermitLimit` | `20` | Max `/api/chat` requests per user per window |
| `RateLimiting:ChatWindowSeconds` | `60` | Sliding-window length for the chat rate limit |
| `Oidc:Authority` | — | Keycloak realm URL used for discovery and token validation |
| `Oidc:PublicOrigin` | — | Browser-facing Keycloak origin when it differs from `Authority` |
| `Oidc:ClientId` / `Oidc:ClientSecret` | — | OIDC client credentials |
| `Oidc:AdminRole` | `cortex-admin` | Keycloak realm role required for admin endpoints |

### Secrets and host-level variables (`.env`)

Compose reads secrets from a git-ignored `.env` file — copy `.env.example` and fill in. Never put secrets in `appsettings.json` or the compose file.

| Variable | Used by | Description |
|----------|---------|-------------|
| `KEYCLOAK_ADMIN_PASSWORD` | keycloak | Admin console password (default `admin`) |
| `OIDC_CLIENT_SECRET` | app | Secret of the `cortex-app` client |
| `OIDC_AUTHORITY` | app (production) | HTTPS Keycloak realm URL |
| `OIDC_PUBLIC_ORIGIN` | app (production) | Browser-facing Keycloak origin, if different |
| `ADO_BASE_URL` / `ADO_PROJECT` / `ADO_REPOSITORY` / `ADO_PATH` / `ADO_BRANCH` | app | Azure DevOps connector (leave empty to ingest `./docs`) |
| `ADO_PAT` | app | Azure DevOps personal access token |
| `ADO_WEBHOOK_SECRET` | app | HMAC-SHA1 secret for `/api/ado-webhook` |
| `GITHUB_OWNER` / `GITHUB_REPOSITORY` / `GITHUB_PATH` / `GITHUB_BRANCH` | app | GitHub connector (leave empty to skip) |
| `GITHUB_PAT` | app | GitHub personal access token (repo read scope) |
| `GITHUB_WEBHOOK_SECRET` | app | HMAC-SHA256 secret for `/api/github-webhook` |
| `KEYCLOAK_PUBLIC_URL` | app (codespaces) | Written automatically by `start-codespaces.sh` |

For overrides that shouldn't be committed (extra ports, different volumes, more env vars), use a `docker-compose.override.yml` — compose merges it automatically:

```yaml
# docker-compose.override.yml (git-ignore it if it contains secrets)
services:
  app-local:
    environment:
      Ollama__ChatModel: llama3.2
      Rag__TopK: "8"
```

### Operational endpoints

| Endpoint | Auth | Description |
|----------|------|-------------|
| `GET /health` | none | Liveness — process is up |
| `GET /health/ready` | none | Readiness — Ollama reachability + ingestion state (JSON) |
| `GET /api/ingest/status` | user | State of the background startup ingestion |
| `POST /api/ingest` | admin | Re-scan and re-index the docs folder |
| `POST /api/ado-webhook` | HMAC-SHA1 | Azure DevOps push event → re-sync and re-index |
| `POST /api/github-webhook` | HMAC-SHA256 | GitHub push event → re-sync and re-index |
| `GET /api/admin/feedback` | admin | Aggregated thumbs-up/down stats |

---

## 3a. Production Profile (Docker + nginx TLS)

The `production` profile adds an nginx reverse proxy that terminates TLS on port 443 and forwards to the app (which has no published host port).

```bash
./scripts/gen-certs.sh my-host.example.com   # self-signed cert for LAN/intranet use
cp .env.example .env                          # set OIDC_AUTHORITY, OIDC_CLIENT_SECRET, ...
docker compose --profile production up -d --build
```

Production enforces `RequireHttpsMetadata=true`, so `OIDC_AUTHORITY` must be an **HTTPS** Keycloak URL whose certificate the app container trusts. For public deployments replace the self-signed cert with a real one (mount into `nginx/certs/`, or add a certbot sidecar).

---

## 4. Publishing to IIS

### Install the .NET Hosting Bundle

On the IIS server, download and run the [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0) (includes the ASP.NET Core runtime and IIS integration module).

### Publish

```bash
dotnet publish src/RagAssistant.Web -c Release -o ./publish
```

### Deploy

1. Copy the `publish/` folder to the IIS server (e.g. `C:\inetpub\RagAssistant`).
2. In IIS Manager, create a new **Application Pool**:
   - .NET CLR Version: **No Managed Code**
   - Identity: a service account with read access to the docs folder
3. Create a new **Website** or **Application** pointing to `C:\inetpub\RagAssistant`.
4. The `web.config` generated by `dotnet publish` configures the ASP.NET Core Module automatically. No additional `web.config` changes are needed for basic deployment.

### IIS-specific `appsettings.json`

Use absolute paths for IIS (relative paths resolve relative to the app's deploy folder):

```json
{
  "Rag": {
    "DocsFolder": "C:\\internal-docs\\markdown",
    "VectorDbPath": "C:\\inetpub\\RagAssistant\\rag_store.db"
  }
}
```

Or override via IIS environment variables in the Application Pool's **Advanced Settings → Environment Variables**.

### Ensure Ollama Is Running

Ollama must be running on the server (or accessible over the network). For a server install, configure Ollama as a Windows Service:

```powershell
# After installing Ollama on Windows
$OllamaPath = (Get-Command ollama).Source
New-Service -Name "Ollama" -BinaryPathName "$OllamaPath serve" -StartupType Automatic
Start-Service Ollama
```

If Ollama runs on a different host, update `Ollama:BaseUrl` accordingly.

---

## 5. Chunking Strategy

Documents are split by ATX heading boundaries (`#`, `##`, etc.), keeping track of the full heading breadcrumb (e.g. "Getting Started > Installation > Windows"). Each heading section becomes one chunk; if a section exceeds `ChunkSize` characters it is further split at paragraph (double-newline) boundaries, and at character level if a single paragraph is too large.

Each chunk is stored with a `[Section: breadcrumb]` prefix so the retriever has heading context even when the chunk is surfaced without its surrounding document.

A small overlap (`ChunkOverlap` chars from the end of the previous chunk) is prepended to each subsequent chunk to prevent sentences that straddle boundaries from being absent from both chunks' embeddings.

---

## 6. Note on Package Versions

`Microsoft.SemanticKernel.Connectors.SqliteVec` and `Microsoft.Extensions.VectorData.Abstractions` are preview packages whose APIs change between releases. If you update to a newer version, check the `VectorStoreCollection<TKey, TRecord>` abstract class API — it consolidates both CRUD and vector search.

The `SQLitePCLRaw.lib.e_sqlite3` dependency has a known vulnerability advisory. For a proof-of-concept running on an internal network this is acceptable; monitor for an updated package before deploying in a higher-risk environment.

---

## 7. Testing

```bash
dotnet test src/RagAssistant.Tests
```

The suite covers chunking/front-matter parsing, hybrid retrieval (RRF fusion, tag filters, history windowing, reranking, HyDE), the FTS5 index, conversation storage, and webhook signature validation. CI runs it on every PR.

## Contributing & License

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for setup, conventions, and the PR process. Released under the [MIT License](LICENSE).
