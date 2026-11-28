FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/HackerNewsBestStories.Worker/ ./Worker/
RUN dotnet publish Worker/HackerNewsBestStories.Worker.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "HackerNewsBestStories.Worker.dll"]
