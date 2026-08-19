# ---------------------------------------------------------------------------
# Agent API — the only surface exposed to the endpoint network (ADR-0001).
#
# Build context is the REPOSITORY ROOT:
#
#   docker build -f server/AgentApi/Dockerfile .
#
# This host must be given the SAME ENDPOINTPLATFORM_SecretProtection__Key as the
# Admin API: the Admin API seals ephemeral secrets and this process redeems
# them. Different keys fail at redemption, not at startup — see
# docs/deployment.md.
# ---------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY shared/Contracts/EndpointPlatform.Contracts.csproj shared/Contracts/
COPY server/Domain/EndpointPlatform.Domain.csproj server/Domain/
COPY server/Infrastructure/EndpointPlatform.Infrastructure.csproj server/Infrastructure/
COPY server/AgentApi/EndpointPlatform.AgentApi.csproj server/AgentApi/
RUN dotnet restore server/AgentApi/EndpointPlatform.AgentApi.csproj

COPY shared/ shared/
COPY server/Domain/ server/Domain/
COPY server/Infrastructure/ server/Infrastructure/
COPY server/AgentApi/ server/AgentApi/
RUN dotnet publish server/AgentApi/EndpointPlatform.AgentApi.csproj \
        -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --system --uid 5002 --create-home appuser
USER appuser

COPY --from=build --chown=appuser:appuser /app ./

ENV ASPNETCORE_URLS=http://+:8081 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8081

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8081/health/ready || exit 1

ENTRYPOINT ["dotnet", "EndpointPlatform.AgentApi.dll"]
