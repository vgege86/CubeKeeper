# syntax=docker/dockerfile:1

# ---------- Compilación ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restaurar primero (capa cacheable).
COPY Directory.Build.props Directory.Packages.props CubeKeeper.slnx ./
COPY src/CubeKeeper.Core/CubeKeeper.Core.csproj src/CubeKeeper.Core/
COPY src/CubeKeeper.Infrastructure/CubeKeeper.Infrastructure.csproj src/CubeKeeper.Infrastructure/
COPY src/CubeKeeper.Web/CubeKeeper.Web.csproj src/CubeKeeper.Web/
RUN dotnet restore src/CubeKeeper.Web/CubeKeeper.Web.csproj

COPY src/ src/
ARG VERSION=0.1.0
# Sin --no-restore a propósito: con él, el publish termina bien pero la imagen sale sin
# wwwroot/_framework/blazor.web.js y la interfaz deja de ser interactiva (lección de Home365).
RUN dotnet publish src/CubeKeeper.Web/CubeKeeper.Web.csproj \
        -c Release -o /app \
        -p:UseAppHost=false -p:Version=${VERSION}
RUN test -f /app/wwwroot/_framework/blazor.web.js

# ---------- Ejecución ----------
# En .NET 10 la imagen aspnet por defecto es Ubuntu 24.04 (noble), la misma base para la que
# Mojang compila bedrock_server. No se usa la variante "chiseled": BDS necesita librerías del sistema.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# curl: healthcheck del contenedor (y arrastra libcurl4t64, que usa bedrock_server).
# tzdata: zona horaria (TZ) para las tareas programadas y los registros.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    CUBEKEEPER_PORT=8765 \
    CUBEKEEPER_DATA=/data \
    TZ=Europe/Madrid \
    HOME=/tmp

WORKDIR /app
COPY --from=build /app ./

# El chown va antes de VOLUME: después, Docker descarta los cambios en esa ruta.
RUN mkdir -p /data && chown 568:568 /data

# 568 = usuario "apps" de TrueNAS. Se puede cambiar con "user:" en el compose.
USER 568:568
VOLUME ["/data"]
EXPOSE 8765/tcp 19132/tcp 19140/udp 7551/udp

HEALTHCHECK --interval=30s --timeout=5s --start-period=2m --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${CUBEKEEPER_PORT}/health" || exit 1

ENTRYPOINT ["dotnet", "CubeKeeper.Web.dll"]
