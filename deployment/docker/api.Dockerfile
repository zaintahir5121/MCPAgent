# Build context: repository root.  docker build -f deployment/docker/api.Dockerfile -t advisory-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/Avantra.AIAdvisory.Api/Avantra.AIAdvisory.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Vendor knowledge packs (catalogue-as-code). In production, documents are ingested by the ingestion job;
# packs are still read from here until the SQL catalogue (Phase 2) replaces them.
COPY knowledge/ ./knowledge/
ENV ASPNETCORE_URLS=http://+:8080 \
    Knowledge__PacksPath=/app/knowledge/vendors
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Avantra.AIAdvisory.Api.dll"]
