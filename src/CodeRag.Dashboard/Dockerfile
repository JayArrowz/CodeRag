# Multi-stage build for the CodeRag Blazor dashboard.
# Build from the repository root:  docker build -f src/CodeRag.Dashboard/Dockerfile .

FROM node:20-slim AS node-deps
WORKDIR /app/tools/ts-analyzer
COPY tools/ts-analyzer/ ./
RUN npm ci --omit=dev

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore (cache-friendly: copy only csprojs first)
COPY CodeRag.sln ./
COPY src/CodeRag.Core/CodeRag.Core.csproj                   src/CodeRag.Core/
COPY src/CodeRag.Analyzers/CodeRag.Analyzers.csproj         src/CodeRag.Analyzers/
COPY src/CodeRag.Storage/CodeRag.Storage.csproj             src/CodeRag.Storage/
COPY src/CodeRag.Dashboard/CodeRag.Dashboard.csproj         src/CodeRag.Dashboard/
RUN dotnet restore src/CodeRag.Dashboard/CodeRag.Dashboard.csproj

# Build + publish
COPY . .
RUN dotnet publish src/CodeRag.Dashboard/CodeRag.Dashboard.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Install Node.js 20 (required for the TypeScript analyzer sidecar)
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

# Copy ts-analyzer with pre-installed node_modules from the node-deps stage
COPY --from=node-deps /app/tools/ts-analyzer ./tools/ts-analyzer

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CodeRag.Dashboard.dll"]
