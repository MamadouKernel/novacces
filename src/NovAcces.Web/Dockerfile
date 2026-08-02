# NovAcces.Web — build à exécuter depuis la RACINE du dépôt :
#   docker build -f src/NovAcces.Web/Dockerfile -t novacces-web .
#
# Le CSS Tailwind (wwwroot/app.css) est déjà compilé et versionné dans le
# dépôt : aucun besoin de Node/npm dans l'image (voir NovAcces.Web.csproj,
# cible BuildTailwindCss — conditionnée à un artefact Windows, ignorée ici).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/NovAcces.Web/NovAcces.Web.csproj", "src/NovAcces.Web/"]
COPY ["src/NovAcces.Shared/NovAcces.Shared.csproj", "src/NovAcces.Shared/"]
RUN dotnet restore "src/NovAcces.Web/NovAcces.Web.csproj"

COPY src/NovAcces.Web/ src/NovAcces.Web/
COPY src/NovAcces.Shared/ src/NovAcces.Shared/

RUN dotnet publish "src/NovAcces.Web/NovAcces.Web.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

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

ENTRYPOINT ["dotnet", "NovAcces.Web.dll"]
