# dp-sync: интеграционный воркер (адаптеры 1С/Битрикс + outbox publisher).
# Контекст сборки — корень репозитория (нужен проект Dp.Core):
#   build: { context: ., dockerfile: src/Dp.Sync/Dockerfile }
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY DataPlatformMvp.sln .
COPY src/Dp.Core ./src/Dp.Core
COPY src/Dp.Sync ./src/Dp.Sync
RUN dotnet publish src/Dp.Sync/Dp.Sync.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
# SD-документы монтируются томом ./deploy/sd:/app/sd:ro (см. docker-compose.yml)
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dp.Sync.dll"]
