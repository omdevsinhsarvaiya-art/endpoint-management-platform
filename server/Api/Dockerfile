# ---------------------------------------------------------------------------
# Admin API — the administrator/dashboard surface.
#
# Build context is the REPOSITORY ROOT, not this directory, because the project
# references Domain, Infrastructure and shared/Contracts:
#
#   docker build -f server/Api/Dockerfile .
#
# Carries no configuration. Every credential arrives through the environment at
# run time (see docs/deployment.md); nothing is baked into the image.
# ---------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only change does not
# invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY shared/Contracts/EndpointPlatform.Contracts.csproj shared/Contracts/
COPY server/Domain/EndpointPlatform.Domain.csproj server/Domain/
COPY server/Infrastructure/EndpointPlatform.Infrastructure.csproj server/Infrastructure/
COPY server/Api/EndpointPlatform.Api.csproj server/Api/
RUN dotnet restore server/Api/EndpointPlatform.Api.csproj

COPY shared/ shared/
COPY server/Domain/ server/Domain/
COPY server/Infrastructure/ server/Infrastructure/
COPY server/Api/ server/Api/
RUN dotnet publish server/Api/EndpointPlatform.Api.csproj \
        -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is here only so the healthcheck below has something to run.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Never root: this process needs no privilege beyond binding a high port and
# talking to PostgreSQL and Redis.
# The package-content directory is owned by the runtime user BEFORE it becomes
# a volume mount point: a named volume's first use copies this directory's
# ownership into the volume, so package/agent-release uploads can write there.
# A root-owned mount point gave the non-root API EACCES on the very first real
# upload (existing volumes need a one-time chown; new ones inherit this).
RUN useradd --system --uid 5001 --create-home appuser     && mkdir -p /var/lib/endpoint-platform/packages     && chown -R appuser:appuser /var/lib/endpoint-platform
USER appuser

COPY --from=build --chown=appuser:appuser /app ./

# Behind nginx, so plain HTTP on the internal network. TLS terminates at the
# proxy; this port is not published to the internet.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080

# /health/ready checks PostgreSQL and Redis, so an unhealthy backing service
# marks the container unhealthy rather than letting it serve broken pages.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "EndpointPlatform.Api.dll"]
