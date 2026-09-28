# Build-Kontext ist das Repo-Root (siehe docker/docker-compose.yml), da das Data-Projekt
# db/migrations/ als eingebettete Ressource referenziert (KONZEPT.md Abschnitt 7/8).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ComdirectFetch.slnx ./
COPY src/ComdirectFetch.Domain/ComdirectFetch.Domain.csproj src/ComdirectFetch.Domain/
COPY src/ComdirectFetch.Api/ComdirectFetch.Api.csproj src/ComdirectFetch.Api/
COPY src/ComdirectFetch.Data/ComdirectFetch.Data.csproj src/ComdirectFetch.Data/
COPY src/ComdirectFetch.Worker/ComdirectFetch.Worker.csproj src/ComdirectFetch.Worker/
COPY src/ComdirectFetch.Tests/ComdirectFetch.Tests.csproj src/ComdirectFetch.Tests/
RUN dotnet restore src/ComdirectFetch.Worker/ComdirectFetch.Worker.csproj

COPY src/ src/
COPY db/ db/
RUN dotnet publish src/ComdirectFetch.Worker/ComdirectFetch.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Security-Review-Fund: lief bisher als root (kein USER, also Default des Basisimages). Das
# aspnet-Basisimage bringt bereits einen dedizierten, unprivilegierten Nutzer mit (live geprüft:
# "app", UID/GID 1654, als $APP_UID exportiert) - Port 8080 braucht ohnehin keine Root-Rechte
# (nur Ports < 1024 tun das). Die kopierten Dateien unter /app bleiben root:root, sind aber
# world-readable (COPY-Standardrechte), reichen also zum Lesen/Ausführen; geschrieben wird zur
# Laufzeit nirgends unter /app (Migrationen sind eingebettete Ressourcen, keine Dateien).
USER $APP_UID

ENTRYPOINT ["dotnet", "ComdirectFetch.Worker.dll"]
