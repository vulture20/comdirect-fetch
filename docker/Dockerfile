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

ENTRYPOINT ["dotnet", "ComdirectFetch.Worker.dll"]
