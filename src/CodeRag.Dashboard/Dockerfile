# Multi-stage build for the CodeRag Blazor dashboard.
# Build from the repository root:  docker build -f src/CodeRag.Dashboard/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore (cache-friendly: copy only csprojs first)
COPY CodeRag.sln ./
COPY src/CodeRag.Core/CodeRag.Core.csproj                   src/CodeRag.Core/
COPY src/CodeRag.Analyzers/CodeRag.Analyzers.csproj         src/CodeRag.Analyzers/
COPY src/CodeRag.Storage/CodeRag.Storage.csproj             src/CodeRag.Storage/
COPY src/CodeRag.Cli/CodeRag.Cli.csproj                     src/CodeRag.Cli/
COPY src/CodeRag.Dashboard/CodeRag.Dashboard.csproj         src/CodeRag.Dashboard/
RUN dotnet restore src/CodeRag.Dashboard/CodeRag.Dashboard.csproj

# Build + publish
COPY . .
RUN dotnet publish src/CodeRag.Dashboard/CodeRag.Dashboard.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CodeRag.Dashboard.dll"]
