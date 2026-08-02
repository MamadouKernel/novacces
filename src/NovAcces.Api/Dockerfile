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
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --system --create-home --shell /usr/sbin/nologin novacces
COPY --from=build /app/publish .
RUN chown -R novacces:novacces /app
USER novacces

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "NovAcces.Api.dll"]
