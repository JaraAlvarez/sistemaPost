# syntax=docker/dockerfile:1
# Servidor de licencias (Fase 12-A): API del POS (/v1), API interna (/admin) y portal web en un solo proceso.
# Contexto de construcción: la RAÍZ del repositorio (usa global.json, Directory.*.props y los proyectos compartidos).
#   docker build -f src/Cloud/Pos.Cloud.Host/Dockerfile -t pos-cloud:local .
# La imagen NO contiene secretos: cadenas de conexión por variables de entorno y la clave privada de firma montada como
# secreto de solo lectura (ver deploy/cloud/docker-compose.yml y docs/despliegue-nube.md).

# ---------- Etapa 1: compilación ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Configuración común (versión del SDK, paquetes centralizados, estilo de código que la compilación exige).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./

# Solo el código que necesita el host de la nube (el POS de escritorio y sus módulos no entran en la imagen).
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Shared/ src/Shared/
COPY src/Server/Pos.Server.Migrations/ src/Server/Pos.Server.Migrations/
COPY src/Cloud/ src/Cloud/

RUN dotnet restore src/Cloud/Pos.Cloud.Host/Pos.Cloud.Host.csproj
RUN dotnet publish src/Cloud/Pos.Cloud.Host/Pos.Cloud.Host.csproj \
        --configuration Release --no-restore --output /app \
        -p:UseAppHost=false -p:GenerateDocumentationFile=false

# ---------- Etapa 2: ejecución ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Directorio persistente de las llaves de Data Protection (cifran el secreto TOTP): se crea con el dueño del usuario no root
# para que el volumen con nombre herede los permisos.
# libgssapi-krb5-2: Npgsql/.NET la buscan al abrir conexiones (sin ella registran "Cannot load library libgssapi_krb5.so.2").
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/pos-cloud/dataprotection \
    && chown -R "$APP_UID" /var/lib/pos-cloud \
    && chmod 700 /var/lib/pos-cloud/dataprotection

WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1 \
    Cloud__DataProtectionKeysPath=/var/lib/pos-cloud/dataprotection

# Usuario sin privilegios que traen las imágenes oficiales de .NET (UID 1654).
USER $APP_UID
EXPOSE 8080
# /var/lib/pos-cloud/dataprotection se monta como volumen con nombre en el compose (sin VOLUME aquí para no crear volúmenes
# anónimos en db-init ni en los comandos de consola).

# Sonda de vida con la consola del propio host (la imagen no trae curl ni wget).
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/Pos.Cloud.Host.dll", "healthcheck", "--url", "http://localhost:8080/health/live"]

ENTRYPOINT ["dotnet", "/app/Pos.Cloud.Host.dll"]
