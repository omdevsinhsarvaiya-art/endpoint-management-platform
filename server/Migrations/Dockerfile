# ---------------------------------------------------------------------------
# Migration job — runs once per deployment, then exits.
#
# Not one of the three long-running services, but the stack cannot initialise
# without it: it applies migrations, re-applies the runtime role's grants and
# reseeds reference data (all idempotent). The APIs deliberately run under the
# RESTRICTED database role and hold no DDL rights, so nothing else in the
# deployment is able to create the schema.
#
# Build context is the REPOSITORY ROOT:
#
#   docker build -f server/Migrations/Dockerfile .
#
# This is the ONLY component given the owner database credential, and it is not
# a service — compose runs it to completion and the APIs wait for its success.
# ---------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY shared/Contracts/EndpointPlatform.Contracts.csproj shared/Contracts/
COPY server/Domain/EndpointPlatform.Domain.csproj server/Domain/
COPY server/Infrastructure/EndpointPlatform.Infrastructure.csproj server/Infrastructure/
COPY server/Migrations/EndpointPlatform.Migrations.csproj server/Migrations/
RUN dotnet restore server/Migrations/EndpointPlatform.Migrations.csproj

COPY shared/ shared/
COPY server/Domain/ server/Domain/
COPY server/Infrastructure/ server/Infrastructure/
COPY server/Migrations/ server/Migrations/
RUN dotnet publish server/Migrations/EndpointPlatform.Migrations.csproj \
        -c Release -o /app --no-restore

# aspnet, not runtime: this is a console app, but it references Infrastructure,
# which carries a FrameworkReference on Microsoft.AspNetCore.App. On the plain
# runtime image the host starts and then fails with "No frameworks were found".
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN useradd --system --uid 5003 --create-home appuser
USER appuser

COPY --from=build --chown=appuser:appuser /app ./

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# A non-zero exit must abort the deployment rather than be ignored; compose is
# configured with service_completed_successfully for exactly this reason.
ENTRYPOINT ["dotnet", "EndpointPlatform.Migrations.dll"]
