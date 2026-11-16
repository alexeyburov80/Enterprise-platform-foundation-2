# Универсальный Dockerfile всех сервисов Dp.Platform (DRY):
# один образ-рецепт, различаются только PROJECT_PATH и APP_DLL.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT_PATH
WORKDIR /src

# Вендоренные зависимости (Npgsql, RabbitMQ.Client) — сборка без NuGet-сети.
COPY lib/ ./lib/
COPY Directory.Build.props ./
COPY src/ ./src/

RUN dotnet publish src/${PROJECT_PATH} -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG APP_DLL=App.dll
ENV APP_DLL=${APP_DLL} \
    ASPNETCORE_URLS=http://+:8080

# curl нужен compose-healthcheck'ам (/health каждого сервиса).
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
CMD dotnet ${APP_DLL}
