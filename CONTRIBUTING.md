# Contributing to Cortex

Thanks for your interest in improving Cortex! This document explains how to get a development environment running and what we expect from contributions.

## Development setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/) (frontend build)
- [Docker + Docker Compose](https://docs.docker.com/) (full stack: Ollama, Keycloak, observability)

### Quick start

```bash
# Full stack in Docker (recommended for first run)
./start-local.sh                       # or: docker compose --profile local up -d --build

# Backend only, against an already-running Ollama/Keycloak
dotnet run --project src/RagAssistant.Web

# Frontend with hot reload (proxies /api to the backend)
cd src/RagAssistant.Web/ClientApp
npm install && npm run dev
```

### Running tests

```bash
dotnet test src/RagAssistant.Tests
```

Tests must pass before a PR is merged. New behaviour needs new tests — see `src/RagAssistant.Tests/` for the existing patterns (xUnit + NSubstitute + FluentAssertions, temp-file SQLite for storage tests).

## Project layout

| Path | Purpose |
|------|---------|
| `src/RagAssistant.Core` | RAG pipeline, ingestion, connectors, conversations — no ASP.NET dependency |
| `src/RagAssistant.Web` | Minimal-API host, auth, SSE endpoints, health checks |
| `src/RagAssistant.Web/ClientApp` | React 19 + Vite + Tailwind SPA, built into `wwwroot` |
| `src/RagAssistant.Tests` | Test suite |

## Coding conventions

- **C#**: idiomatic modern C# (primary constructors, collection expressions, file-scoped namespaces). Match the style of the file you are editing. Comments explain *why*, not *what*.
- **TypeScript/React**: functional components, Zustand for state, TanStack Query for server data. Tailwind utility classes over custom CSS.
- **No secrets in code or config files** — secrets come from environment variables (`.env`, documented in `.env.example`).
- Keep `Core` free of ASP.NET types so it stays independently testable.

## Adding a document connector

Implement `IDocumentSource` (`Name`, `IsConfigured`, `SyncAsync()` returning `IAsyncEnumerable<RawDocument>`) and register it in `Program.cs`. `DocumentSourceSynchronizer` handles mirroring, stale-file cleanup, and ingestion — your connector only lists and downloads documents. See `GitHubIngestionService` for the reference implementation.

## Pull request process

1. Fork and create a topic branch from `master`.
2. Make your change, with tests.
3. Ensure `dotnet build`, `dotnet test`, and `npm run build` (in `ClientApp`) all pass — CI runs exactly these.
4. Open a PR with a clear description of the problem and solution. Small, focused PRs review faster than large ones.

## Reporting issues

Use the issue templates. For security-sensitive reports (auth bypass, secret leakage), please do not open a public issue — contact the maintainer directly instead.
