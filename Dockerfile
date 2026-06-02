# syntax=docker/dockerfile:1
#
# Single-service (same-origin) image for Railway: the .NET API serves the built React SPA from wwwroot,
# so REST + the cascade WebSocket + the static app all share one origin (no CORS, no cross-origin WS).
# Build context = repo root (it needs web/, server/, and config/).

# ---- Stage 1: build the React/Vite SPA ----
FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# Built with no VITE_API_BASE_URL → clients use relative paths + derive wss:// from window.location,
# i.e. same-origin as wherever this image serves the SPA. (toWebSocketUrl('', location) → wss://<host>/…)
RUN npm run build         # tsc --noEmit && vite build → /web/dist

# ---- Stage 2: restore + publish the .NET API ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
# Restore first so this layer caches when only source (not the manifest/SDK pin) changes.
COPY server/global.json ./global.json
COPY server/AiInterpreter.Api/AiInterpreter.Api.csproj ./AiInterpreter.Api/AiInterpreter.Api.csproj
RUN dotnet restore AiInterpreter.Api/AiInterpreter.Api.csproj
COPY server/AiInterpreter.Api/ ./AiInterpreter.Api/
RUN dotnet publish AiInterpreter.Api/AiInterpreter.Api.csproj -c Release -o /app/publish --no-restore
# Drop the SPA build into wwwroot so the API serves it same-origin (Program.cs self-gates on its presence).
COPY --from=web /web/dist /app/publish/wwwroot

# ---- Stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=api /app/publish ./
# pricing.json is read via PRICING_CONFIG_PATH; evaluation-phrases.json already rides the publish output
# (csproj CopyToOutputDirectory). Create a writable session dir (NOTE: ephemeral on Railway unless a
# volume is mounted at /app/data — fine for an evaluation/demo workbench; sessions are transient evidence).
COPY config/ ./config/
RUN mkdir -p /app/data/sessions
ENV ASPNETCORE_ENVIRONMENT=Production \
    PRICING_CONFIG_PATH=/app/config/pricing.json \
    SESSION_DATA_DIR=/app/data/sessions
# Railway injects $PORT at runtime; bind Kestrel to it (default 8080 for a plain `docker run`).
EXPOSE 8080
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} dotnet AiInterpreter.Api.dll"]
