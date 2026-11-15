# dp-api: публичный Data Access API + admin. Контекст сборки — корень репозитория
# (нужен проект Dp.Core), dockerfile указывается в docker-compose.yml:
#   build: { context: ., dockerfile: src/Dp.Api/Dockerfile }
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY DataPlatformMvp.sln .
COPY src/Dp.Core ./src/Dp.Core
COPY src/Dp.Api ./src/Dp.Api
RUN dotnet publish src/Dp.Api/Dp.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
# SD-документы монтируются томом ./deploy/sd:/app/sd:ro (см. docker-compose.yml)
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dp.Api.dll"]
