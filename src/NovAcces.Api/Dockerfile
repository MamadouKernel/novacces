# NovAcces.Api — build à exécuter depuis la RACINE du dépôt :
#   docker build -f src/NovAcces.Api/Dockerfile -t novacces-api .
# (nécessaire pour que les COPY ci-dessous voient les autres projets référencés).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copie sélective des .csproj d'abord : le cache Docker ne revalide "dotnet
# restore" que si l'un de ces fichiers change, pas à chaque modification de code.
COPY ["src/NovAcces.Api/NovAcces.Api.csproj", "src/NovAcces.Api/"]
COPY ["src/NovAcces.Infrastructure/NovAcces.Infrastructure.csproj", "src/NovAcces.Infrastructure/"]
COPY ["src/NovAcces.Application/NovAcces.Application.csproj", "src/NovAcces.Application/"]
COPY ["src/NovAcces.Domain/NovAcces.Domain.csproj", "src/NovAcces.Domain/"]
COPY ["src/NovAcces.Shared/NovAcces.Shared.csproj", "src/NovAcces.Shared/"]
RUN dotnet restore "src/NovAcces.Api/NovAcces.Api.csproj"

COPY src/NovAcces.Api/ src/NovAcces.Api/
COPY src/NovAcces.Infrastructure/ src/NovAcces.Infrastructure/
COPY src/NovAcces.Application/ src/NovAcces.Application/
COPY src/NovAcces.Domain/ src/NovAcces.Domain/
COPY src/NovAcces.Shared/ src/NovAcces.Shared/

RUN dotnet publish "src/NovAcces.Api/NovAcces.Api.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl : uniquement pour le HEALTHCHECK ci-dessous (image aspnet ne l'inclut pas).
# postgresql-client-16 : fournit pg_dump, utilisé par IDatabaseBackupService
# pour les sauvegardes déclenchées par le SuperAdmin — version alignée sur
# "postgres:16-alpine" du docker-compose (pg_dump doit être de version >= au
# serveur qu'il sauvegarde). Le dépôt PGDG est nécessaire : Debian bookworm
# ("mcr.microsoft.com/dotnet/aspnet:8.0") ne fournit pas nativement la
# version 16 du client.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gnupg lsb-release \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/keyrings/postgresql.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/postgresql.gpg] http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-16 \
    && apt-get purge -y gnupg lsb-release \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --system --create-home --shell /usr/sbin/nologin novacces
COPY --from=build /app/publish .
# Répertoire des clés DataProtection : monté en volume nommé (docker-compose)
# pour survivre à la recréation du conteneur — sans quoi chaque redéploiement
# invalide les jetons en cours (réinitialisation de mot de passe, etc.).
RUN mkdir -p /app/keys /app/backups
RUN chown -R novacces:novacces /app
USER novacces

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "NovAcces.Api.dll"]
