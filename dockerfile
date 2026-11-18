# Etapa 1: Compilar y publicar la API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/HackerNewsBestStories.Api/*.csproj ./HackerNewsBestStories.Api/
RUN dotnet restore ./HackerNewsBestStories.Api/HackerNewsBestStories.Api.csproj

COPY src/HackerNewsBestStories.Api/ ./HackerNewsBestStories.Api/
RUN dotnet publish ./HackerNewsBestStories.Api/HackerNewsBestStories.Api.csproj -c Release -o /app/publish --no-restore

# Etapa 2: Ejecutar la aplicación
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "HackerNewsBestStories.Api.dll"]
