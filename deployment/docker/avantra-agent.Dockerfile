# AvantraAgent production image. Build context: repository root.
#   docker build -f deployment/docker/avantra-agent.Dockerfile -t avantra-agent .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/AvantraAgent/AvantraAgent.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Vendor packs (product catalogues) and seed documents. Mount a volume or use Blob/AI Search for everything else.
COPY knowledge/ ./knowledge/
RUN mkdir -p /app/data/staging && chown -R $APP_UID /app/data
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Knowledge__PacksPath=/app/knowledge/vendors \
    Ingestion__AllowedSourceRoot=/app/knowledge \
    Ingestion__StagingPath=/app/data/staging
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AvantraAgent.dll"]
