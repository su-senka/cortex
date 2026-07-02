# ── Stage 1: Build the React SPA ──────────────────────────────────────────────
FROM node:22-slim AS spa
# Mirror the source tree so vite's outDir: '../wwwroot' resolves correctly.
WORKDIR /repo/src/RagAssistant.Web/ClientApp
COPY src/RagAssistant.Web/ClientApp/package*.json ./
RUN npm ci --prefer-offline
COPY src/RagAssistant.Web/ClientApp/ ./
RUN npm run build
# Output lands at /repo/src/RagAssistant.Web/wwwroot/

# ── Stage 2: Build the .NET app ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as a separate layer — re-runs only when csproj files change.
COPY src/RagAssistant.Core/RagAssistant.Core.csproj src/RagAssistant.Core/
COPY src/RagAssistant.Web/RagAssistant.Web.csproj   src/RagAssistant.Web/
RUN dotnet restore src/RagAssistant.Web/RagAssistant.Web.csproj

COPY src/ src/
# Overlay the SPA build output so dotnet publish picks up the built assets.
COPY --from=spa /repo/src/RagAssistant.Web/wwwroot/ src/RagAssistant.Web/wwwroot/
RUN dotnet publish src/RagAssistant.Web/RagAssistant.Web.csproj \
    --no-restore -c Release -o /out -p:SkipSpa=true

# ── Stage 3: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /out ./

# Volume-mount targets created here so Docker doesn't create them as root-owned.
RUN mkdir -p /data /docs

# Defaults match the service names in docker-compose.yml.
# Override via environment variables or a K8s ConfigMap.
ENV Ollama__BaseUrl=http://ollama:11434
ENV Rag__VectorDbPath=/data/rag_store.db
ENV Rag__DocsFolder=/docs

EXPOSE 8080

ENTRYPOINT ["dotnet", "RagAssistant.Web.dll"]