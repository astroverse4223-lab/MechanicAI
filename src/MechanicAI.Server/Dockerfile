# Mechanic AI shop server. Build from the repository root:
#   docker build -f src/MechanicAI.Server/Dockerfile -t mechanicai-server .
# or use src/MechanicAI.Server/docker-compose.yml (server + PostgreSQL/pgvector + optional Redis).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore first so the package layer is cached across source edits.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/MechanicAI.Domain/MechanicAI.Domain.csproj src/MechanicAI.Domain/
COPY src/MechanicAI.Application/MechanicAI.Application.csproj src/MechanicAI.Application/
COPY src/MechanicAI.Infrastructure/MechanicAI.Infrastructure.csproj src/MechanicAI.Infrastructure/
COPY src/MechanicAI.Server/MechanicAI.Server.csproj src/MechanicAI.Server/
# global.json is deliberately not copied: the image tag pins the SDK inside the container.
RUN dotnet restore src/MechanicAI.Server/MechanicAI.Server.csproj

COPY src/MechanicAI.Domain/ src/MechanicAI.Domain/
COPY src/MechanicAI.Application/ src/MechanicAI.Application/
COPY src/MechanicAI.Infrastructure/ src/MechanicAI.Infrastructure/
COPY src/MechanicAI.Server/ src/MechanicAI.Server/
RUN dotnet publish src/MechanicAI.Server/MechanicAI.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Uploaded documents live under /data; mount a volume there.
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__DataRoot=/data

EXPOSE 8080
VOLUME ["/data"]
COPY --from=build /app .
ENTRYPOINT ["dotnet", "MechanicAI.Server.dll"]
