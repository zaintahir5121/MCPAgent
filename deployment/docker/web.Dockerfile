# Build context: repository root.  docker build -f deployment/docker/web.Dockerfile -t advisory-web .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Avantra.AIAdvisory.Domain/ src/Avantra.AIAdvisory.Domain/
COPY src/Avantra.AIAdvisory.Web/ src/Avantra.AIAdvisory.Web/
RUN dotnet publish src/Avantra.AIAdvisory.Web/Avantra.AIAdvisory.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Avantra.AIAdvisory.Web.dll"]
